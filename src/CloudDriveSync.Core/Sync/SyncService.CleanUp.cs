using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.OnDemand;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

public sealed partial class SyncService
{
    /// <summary>Placeholders a program held when a synchronisation with files on demand ended - cleared at the next start.</summary>
    /// <param name="Registration">The registration they belong to; only it makes them valid again.</param>
    /// <param name="Context">The synchronisation's ID, the registration's context.</param>
    internal sealed record PendingCleanUp(string Registration, string Folder, string Context);

    private readonly Lock _cleanUpGate = new();

    /// <summary>How long Windows gets to clear up after a registration ended; tests make it short.</summary>
    internal TimeSpan CleanUpWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often a folder that a program held is tried again while CloudDrive-Sync runs.</summary>
    internal TimeSpan CleanUpInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// What ended synchronisations left is cleared at the start, and then again and again while a program still holds
    /// something of it - once it lets go, the folder is cleared and its registration ends.
    /// </summary>
    private async Task CleanUpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (OnDemandSupported) FinishCleanUps();
                    }, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Warn("Sync", $"Clearing up left-overs failed, tried again later: {e.Message}");
                }
                lock (_cleanUpGate)
                {
                    // Nothing left to clear: the loop ends - a folder noted from now on starts a new one.
                    if (!File.Exists(CleanUpFile))
                    {
                        _cleanUpLoop = null;
                        return;
                    }
                }
                await Task.Delay(CleanUpInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // CloudDrive-Sync ends; the next start goes on.
        }
    }

    /// <summary>Starts trying again when a folder was noted while no loop runs.</summary>
    private void KeepCleaningUp()
    {
        lock (_cleanUpGate)
        {
            if (_cleanUpLoop is not null) return;
            _cleanUpLoop = CleanUpLoopAsync(_shutdown.Token);
        }
    }

    private Task? _cleanUpLoop;

    private string CleanUpFile => CleanUpFileOf(_paths);

    private static string CleanUpFileOf(AppPaths paths) => Path.Combine(paths.Home, "cleanup.json");

    internal static List<PendingCleanUp> LoadCleanUps(string file)
    {
        try
        {
            var pending = JsonSerializer.Deserialize<List<PendingCleanUp>>(File.ReadAllText(file), SettingsStore.JsonOptions)
                ?? throw new JsonException("the list is null");
            if (pending.Any(item => string.IsNullOrWhiteSpace(item.Registration) || string.IsNullOrWhiteSpace(item.Folder) || string.IsNullOrWhiteSpace(item.Context)))
                throw new JsonException("a clean-up entry is incomplete");
            return pending;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (JsonException e)
        {
            // Returning [] would silently discard the only registration IDs that can repair these placeholders.
            throw new IOException($"List of folders to clear up is damaged: {file}", e);
        }
    }

    private void SaveCleanUps(List<PendingCleanUp> pending) => SaveCleanUps(CleanUpFile, pending);

    internal static void SaveCleanUps(string file, List<PendingCleanUp> pending)
    {
        if (pending.Count == 0)
        {
            try { File.Delete(file); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pending, SettingsStore.JsonOptions));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Same directory and volume: readers see either the previous complete list or the new one.
            if (File.Exists(file)) File.Replace(temporary, file, null);
            else File.Move(temporary, file);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Before CloudDrive-Sync is uninstalled: ends this user's registrations of the installed program (never those of
    /// the tests) the same way as ending a synchronisation - dissolved first, ended only when nothing of it is left
    /// (<see cref="Leftovers.EndWhenClear"/>). A folder a program holds stays registered - its placeholders stay
    /// valid and can be deleted - and is noted; should CloudDrive-Sync come back, it clears the folder at its start.
    /// The same happens to a folder the time is not enough for. Returns the IDs that ended.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763")]
    public static IReadOnlyList<string> EndAllForUninstall(AppPaths paths, TimeSpan budget)
        => EndAllForUninstall(paths, budget, SyncRoots.FolderOf);

    /// <summary>The folder resolver is injectable so a missing or unreadable registry value can be tested.</summary>
    [SupportedOSPlatform("windows10.0.17763")]
    internal static IReadOnlyList<string> EndAllForUninstall(AppPaths paths, TimeSpan budget, Func<string, string?> folderOf)
    {
        var deadline = DateTime.UtcNow + budget;
        var pairs = File.Exists(paths.SettingsFile) ? new SettingsStore(paths.SettingsFile).Current.Syncs : [];
        var ended = new List<string>();
        var noted = LoadCleanUpsOrSetAside(CleanUpFileOf(paths));
        foreach (var id in SyncRoots.RegisteredIds(paths.SyncRootProvider))
        {
            string? folder = null;
            // The registration's context is its synchronisation's ID; a registration of its own carries a key after it.
            var context = pairs.FirstOrDefault(p => SyncRoots.IdFor(paths, p) == id)?.Id ?? id.Split('!')[^1];
            try
            {
                folder = folderOf(id);
                if (folder is null)
                {
                    // No known folder is not proof that this registration has no placeholders.
                    Log.Warn("Sync", $"Uninstalling: folder of '{id}' could not be determined; registration stays.");
                }
                else if (DateTime.UtcNow < deadline && Leftovers.EndWhenClear(id, folder, OnDemandPair.CleanUpSpec(id, folder, context), TimeSpan.FromSeconds(3), out _))
                {
                    ended.Add(id);
                }
                else
                {
                    noted.RemoveAll(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase));
                    noted.Add(new PendingCleanUp(id, folder, context));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
            {
                Log.Warn("Sync", $"Uninstalling: '{context}' not ended: {e.Message}");
                if (folder is not null) noted.Add(new PendingCleanUp(id, folder, context));
            }
        }
        SaveCleanUps(CleanUpFileOf(paths), noted);
        return ended;
    }

    /// <summary>
    /// The list of folders to clear up; a damaged one is set aside (kept for a look later, never thrown away) and an
    /// empty list goes on - ending, adding and uninstalling never stop on it. Nothing is lost by that: a folder still
    /// waiting keeps its registration with Windows, and <see cref="FinishCleanUps"/> finds those there, too.
    /// </summary>
    private static List<PendingCleanUp> LoadCleanUpsOrSetAside(string file)
    {
        try
        {
            return LoadCleanUps(file);
        }
        catch (IOException e)
        {
            var aside = $"{file}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(file, aside);
            }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Sync", $"Damaged list of folders to clear up not set aside: {move.Message}");
            }
            Log.Warn("Sync", $"List of folders to clear up was damaged ({e.InnerException?.Message ?? e.Message}); set aside as {Path.GetFileName(aside)}.");
            return [];
        }
    }

    private void RememberCleanUp(PendingCleanUp item)
    {
        lock (_cleanUpGate)
        {
            var pending = LoadCleanUpsOrSetAside(CleanUpFile);
            pending.RemoveAll(p => string.Equals(p.Folder, item.Folder, StringComparison.OrdinalIgnoreCase));
            pending.Add(item);
            SaveCleanUps(pending);
        }
        KeepCleaningUp();
    }

    /// <summary>
    /// Clears placeholders left behind (see <see cref="PendingCleanUp"/>): their registration again for a moment, then
    /// <see cref="Leftovers.Dissolve"/>, then the registration ends. Besides the noted folders, every registration of
    /// CloudDrive-Sync with Windows that no synchronisation uses any more is cleared, too - so nothing stays behind even
    /// when the list was lost. A registration a synchronisation uses, or one that points to another folder by now, is
    /// never touched: the entry waits until that synchronisation ends.
    /// </summary>
    /// <param name="onlyFolder">Only this folder (before a new synchronisation takes it).</param>
    /// <param name="ending">The synchronisation that ends right now - its registration is free again.</param>
    [SupportedOSPlatform("windows10.0.17763")]
    internal void FinishCleanUps(string? onlyFolder = null, string? ending = null)
    {
        lock (_cleanUpGate)
        {
            var pending = LoadCleanUpsOrSetAside(CleanUpFile);
            // Windows first, the settings second: a synchronisation is saved before it registers, so a registration
            // seen here belongs to a synchronisation the settings already show.
            var withWindows = SyncRoots.RegisteredIds(_paths.SyncRootProvider);
            var inUse = _settings.Current.Syncs.Where(p => p.Mode == SyncMode.OnDemand && !string.Equals(p.Id, ending, StringComparison.OrdinalIgnoreCase))
                .Select(p => SyncRoots.IdFor(_paths, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // The registration of the synchronisation ending right now is noted by the ending itself, should it stay.
            var endingId = ending is not null && FindPair(ending) is { } endingPair ? SyncRoots.IdFor(_paths, endingPair) : null;
            foreach (var id in withWindows)
            {
                if (inUse.Contains(id) || string.Equals(id, endingId, StringComparison.OrdinalIgnoreCase)) continue;
                if (pending.Any(p => string.Equals(p.Registration, id, StringComparison.OrdinalIgnoreCase))) continue;
                if (SyncRoots.FolderOf(id) is { } folder) pending.Add(new PendingCleanUp(id, folder, id.Split('!')[^1]));
            }
            if (pending.Count == 0) return;
            var left = new List<PendingCleanUp>();
            foreach (var item in pending)
            {
                if ((onlyFolder is not null && !string.Equals(Path.GetFullPath(item.Folder), Path.GetFullPath(onlyFolder), StringComparison.OrdinalIgnoreCase)) ||
                    inUse.Contains(item.Registration))
                {
                    left.Add(item);
                    continue;
                }
                try
                {
                    var registered = SyncRoots.FolderOf(item.Registration);
                    if (registered is not null && !string.Equals(Path.TrimEndingDirectorySeparator(registered), Path.TrimEndingDirectorySeparator(item.Folder), StringComparison.OrdinalIgnoreCase))
                    {
                        left.Add(item);
                        continue;
                    }
                    if (Leftovers.Count(item.Folder) == 0)
                    {
                        // Nothing left (the program let go, or the folder is gone): the registration that waited for it ends.
                        if (registered is not null) SyncRoots.Unregister(item.Registration);
                        continue;
                    }
                    var again = OnDemandPair.CleanUpSpec(item.Registration, item.Folder, item.Context);
                    // Only its own registration makes a placeholder readable again.
                    if (registered is null) SyncRoots.Register(again);
                    var ended = Leftovers.EndWhenClear(item.Registration, item.Folder, again, CleanUpWait, out var result);
                    Log.Info("Sync", ended
                        ? $"Left-overs of '{item.Context}' cleared: {result.Removed} removed, {result.Kept} kept as normal files."
                        : $"Left-overs of '{item.Context}': {result.Failed.Count} still held by a program; the folder stays registered until they are free.");
                    if (!ended) left.Add(item);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
                {
                    Log.Warn("Sync", $"Left-overs of '{item.Context}' not cleared yet: {e.Message}");
                    left.Add(item);
                }
            }
            SaveCleanUps(left);
        }
    }
}
