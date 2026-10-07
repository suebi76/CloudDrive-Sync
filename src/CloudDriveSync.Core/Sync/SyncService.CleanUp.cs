using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
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
    internal TimeSpan CleanUpWait { get; set; } = TimeSpan.FromSeconds(60);

    private string CleanUpFile => Path.Combine(_paths.Home, "cleanup.json");

    private List<PendingCleanUp> LoadCleanUps()
    {
        try
        {
            if (File.Exists(CleanUpFile)) return JsonSerializer.Deserialize<List<PendingCleanUp>>(File.ReadAllText(CleanUpFile), SettingsStore.JsonOptions) ?? [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Warn("Sync", $"List of folders to clear up unreadable: {e.Message}");
        }
        return [];
    }

    private void SaveCleanUps(List<PendingCleanUp> pending)
    {
        if (pending.Count == 0) File.Delete(CleanUpFile);
        else File.WriteAllText(CleanUpFile, JsonSerializer.Serialize(pending, SettingsStore.JsonOptions));
    }

    private void RememberCleanUp(PendingCleanUp item)
    {
        lock (_cleanUpGate)
        {
            var pending = LoadCleanUps();
            pending.RemoveAll(p => string.Equals(p.Folder, item.Folder, StringComparison.OrdinalIgnoreCase));
            pending.Add(item);
            SaveCleanUps(pending);
        }
    }

    /// <summary>
    /// Clears placeholders left behind (see <see cref="PendingCleanUp"/>): their registration again for a moment, then
    /// <see cref="Leftovers.Dissolve"/>, then the registration ends. A registration a synchronisation uses, or one that
    /// points to another folder by now, is never touched: the entry waits until that synchronisation ends.
    /// </summary>
    /// <param name="onlyFolder">Only this folder (before a new synchronisation takes it).</param>
    /// <param name="ending">The synchronisation that ends right now - its registration is free again.</param>
    [SupportedOSPlatform("windows10.0.17763")]
    internal void FinishCleanUps(string? onlyFolder = null, string? ending = null)
    {
        lock (_cleanUpGate)
        {
            var pending = LoadCleanUps();
            if (pending.Count == 0) return;
            var inUse = _settings.Current.Syncs.Where(p => p.Mode == SyncMode.OnDemand && !string.Equals(p.Id, ending, StringComparison.OrdinalIgnoreCase))
                .Select(p => SyncRoots.IdFor(_paths, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                    if (!Directory.Exists(item.Folder) || Leftovers.Count(item.Folder) == 0) continue;
                    if (SyncRoots.FolderOf(item.Registration) is { } registered)
                    {
                        if (!string.Equals(Path.TrimEndingDirectorySeparator(registered), Path.TrimEndingDirectorySeparator(item.Folder), StringComparison.OrdinalIgnoreCase))
                        {
                            left.Add(item);
                            continue;
                        }
                    }
                    else
                    {
                        SyncRoots.Register(new SyncRootSpec(item.Registration, item.Folder, "CloudDrive-Sync – Aufräumen", $"{Environment.ProcessPath},0", "0", item.Context));
                    }
                    var result = Leftovers.Dissolve(item.Folder);
                    SyncRoots.Unregister(item.Registration);
                    var clear = Leftovers.WaitUntilClear(item.Folder, CleanUpWait);
                    Log.Info("Sync", $"Left-overs of '{item.Context}' cleared: {result.Removed} removed, {result.Kept} kept as normal files{(clear ? "" : ", some still left")}.");
                    if (!clear) left.Add(item);
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
