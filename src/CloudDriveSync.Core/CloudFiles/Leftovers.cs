using System.Runtime.Versioning;

namespace CloudDriveSync.Core.CloudFiles;

/// <summary>What a clean-up did: placeholders removed (online only), turned into normal files, and what it could not do.</summary>
public sealed record CleanUpResult(int Removed, int Kept, IReadOnlyList<string> Failed);

/// <summary>
/// What stays of a folder with files on demand when its synchronisation ends. Windows clears placeholders by itself when
/// a registration ends - but not those a program holds right then (Explorer, the search index): they stay behind,
/// Windows calls them corrupt, and nothing deletes them. So CloudDrive-Sync clears them itself, before and, should any be
/// left, after the registration ends.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public static class Leftovers
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: the marks of a cloud placeholder.
    private const FileAttributes CloudMarks = (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    /// <summary>
    /// Entries below the folder that still carry a placeholder's marks - read from their attributes, nothing is opened. A
    /// folder Windows cannot read (a left-over it calls corrupt) counts by its own marks.
    /// </summary>
    public static int Count(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var count = 0;
        var folders = new Stack<DirectoryInfo>([new DirectoryInfo(folder)]);
        while (folders.Count > 0)
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = folders.Pop().EnumerateFileSystemInfos().ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if ((entry.Attributes & CloudMarks) != 0 || ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && !IsLink(entry))) count++;
                if (entry is DirectoryInfo directory && !IsLink(directory)) folders.Push(directory);
            }
        }
        return count;
    }

    /// <summary>A link to another place (junction, symbolic link) is never followed.</summary>
    private static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns the placeholders below a registered folder into what stays after a synchronisation: an online-only file goes
    /// (its data is in the cloud), a file with its data on the PC - or with a change not uploaded yet - becomes a normal
    /// file, a placeholder folder becomes a normal folder or goes when nothing is left in it. Deepest first; what a program
    /// holds is named in the result and left as it is.
    /// </summary>
    public static CleanUpResult Dissolve(string folder)
    {
        int removed = 0, kept = 0;
        var failed = new List<string>();
        void Visit(string directory)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(Path.GetRelativePath(folder, directory));
                return;
            }
            foreach (var entry in entries)
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (IsLink(new DirectoryInfo(entry))) continue;
                        Visit(entry);
                        if (Placeholders.Read(entry) is null) continue;
                        if (Directory.EnumerateFileSystemEntries(entry).Any())
                        {
                            Placeholders.Revert(entry);
                            kept++;
                        }
                        else
                        {
                            Directory.Delete(entry);
                            removed++;
                        }
                    }
                    else if (Placeholders.Read(entry) is { } file)
                    {
                        if (file.IsFullyOnDisk || !file.InSync)
                        {
                            Placeholders.Revert(entry);
                            kept++;
                        }
                        else
                        {
                            File.Delete(entry);
                            removed++;
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed.Add(Path.GetRelativePath(folder, entry));
                }
            }
        }
        Visit(folder);
        return new CleanUpResult(removed, kept, failed);
    }

    /// <summary>
    /// Ends a registration - but only once nothing of it is left in its folder. A placeholder is valid only while its
    /// registration exists; one left behind after the end is unreadable for good, and Explorer cannot even delete the
    /// folder. So: the placeholders are dissolved first (<see cref="Dissolve"/>, a few attempts); while a program holds
    /// one, the registration stays (named <paramref name="again"/>) and false is returned - the caller clears the folder
    /// later. After the end the folder is checked once more: should Windows have left placeholders after all, the same
    /// registration comes back (only it makes them readable), they are dissolved, and the end follows again.
    /// </summary>
    /// <param name="again">The registration to come back with: same ID and folder, a name that says what goes on.</param>
    /// <param name="wait">How long Windows gets to clear the folder after the end.</param>
    /// <returns>True when the registration ended and the folder holds no placeholder.</returns>
    public static bool EndWhenClear(string id, string? folder, SyncRootSpec again, TimeSpan wait, out CleanUpResult result)
    {
        result = new CleanUpResult(0, 0, []);
        var exists = folder is not null && Directory.Exists(folder);
        for (var round = 0; round < 3; round++)
        {
            if (exists)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var step = Dissolve(folder!);
                    result = new CleanUpResult(result.Removed + step.Removed, result.Kept + step.Kept, step.Failed);
                    if (Count(folder!) == 0) break;
                    if (attempt == 2)
                    {
                        // Held by a program: the registration stays, so every placeholder stays valid and can go later.
                        SyncRoots.Register(again);
                        return false;
                    }
                    Thread.Sleep(1000);
                }
            }
            if (SyncRoots.IsRegistered(id)) SyncRoots.Unregister(id);
            if (!exists || WaitUntilClear(folder!, wait)) return true;
            SyncRoots.Register(again);
        }
        return false;
    }

    /// <summary>
    /// Waits until nothing is left below the folder - Windows clears what it still finds a moment after a registration
    /// ended. False when something stays.
    /// </summary>
    public static bool WaitUntilClear(string folder, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Count(folder) > 0)
        {
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(500);
        }
        return true;
    }
}
