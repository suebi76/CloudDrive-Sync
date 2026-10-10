namespace CloudDriveSync.Core.Sync;

/// <summary>
/// Walks a folder on the PC, all levels, so that nothing in it ends the walk. .NET's own recursive enumeration stops with
/// an exception at the first folder Windows refuses with anything but missing rights (IgnoreInaccessible covers only
/// those) - a placeholder Windows calls broken ("Die Clouddatei-Metadaten sind beschädigt", error 363), a folder a
/// program holds for itself. Such a folder once kept the whole program from starting. Here it is passed over and named;
/// what is in it is unknown, so no caller may take it as empty. The folder itself unreadable throws. Links are not
/// followed (see <see cref="IsLink"/>).
/// </summary>
internal static class FolderWalk
{
    /// <summary>The files and folders below <paramref name="root"/>.</summary>
    /// <param name="skip">Entries with one of these attributes are neither returned nor entered (e.g. links).</param>
    /// <param name="refused">Collects the folders Windows refused (full paths).</param>
    public static IEnumerable<FileSystemInfo> Entries(string root, FileAttributes skip, ICollection<string>? refused = null)
    {
        var options = new EnumerationOptions { AttributesToSkip = skip, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var start = new DirectoryInfo(root);
        var folders = new Stack<DirectoryInfo>([start]);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (DirectoryNotFoundException) when (folder != start)
            {
                // Gone since its parent was read.
                continue;
            }
            catch (Exception e) when (folder != start && e is IOException or UnauthorizedAccessException)
            {
                refused?.Add(folder.FullName);
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo below && !IsLink(below, refused)) folders.Push(below);
                yield return entry;
            }
        }
    }

    /// <summary>
    /// A link (symbolic link, junction) is returned but never followed: what it points to is not part of the folder - a
    /// walk to remove the rest of a folder must never reach beyond it. Cloud placeholders are reparse points, too, but no
    /// links: they are entered.
    /// </summary>
    public static bool IsLink(DirectoryInfo folder, ICollection<string>? refused = null)
    {
        if ((folder.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try
        {
            return folder.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            refused?.Add(folder.FullName);
            return true;
        }
    }

    /// <summary>The files below <paramref name="root"/> (see <see cref="Entries"/>).</summary>
    public static IEnumerable<FileInfo> Files(string root, FileAttributes skip, ICollection<string>? refused = null) =>
        Entries(root, skip, refused).OfType<FileInfo>();
}
