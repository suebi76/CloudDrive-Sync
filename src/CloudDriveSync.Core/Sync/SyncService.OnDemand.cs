using System.Runtime.Versioning;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.OnDemand;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

public sealed partial class SyncService
{
    // The live parts of each synchronisation with files on demand (registration, connection, state); created on first
    // use, which only happens on Windows versions with the Cloud Files API.
    private Dictionary<string, OnDemandPair>? _live;
    private OnDemandRunner? _onDemandRunner;

    /// <summary>The clock for freeing space after some days; tests move it forward.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>The name of a synchronisation's entry in Explorer: the one chosen, or <see cref="DefaultExplorerName"/>.</summary>
    public static string ExplorerNameOf(SyncPairSettings pair, AccountSettings? account) =>
        !string.IsNullOrWhiteSpace(pair.ExplorerName) ? pair.ExplorerName.Trim() : DefaultExplorerName(pair, account);

    /// <summary>"&lt;account&gt; – &lt;folder&gt;", or the account alone when the whole storage is synchronised.</summary>
    public static string DefaultExplorerName(SyncPairSettings pair, AccountSettings? account) =>
        account is null ? "CloudDrive-Sync"
            : pair.RemotePath.Trim('/').Length == 0 ? account.Label
            : $"{account.Label} – {CloudFolderNames.ShowPath(account.Kind, pair.RemotePath)}";

    /// <summary>Files on demand need the Cloud Files API of Windows 10 1809 or later.</summary>
    [SupportedOSPlatformGuard("windows10.0.17763")]
    public static bool OnDemandSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>
    /// Why a folder cannot hold files on demand - not NTFS, not a fixed drive of this PC, a whole drive, or inside a
    /// folder another cloud program (or another synchronisation) has registered with Windows - or null when it can. The
    /// reason is a sentence for the user, like the hints of <see cref="LocalFolderCheck"/>.
    /// </summary>
    public static string? OnDemandProblem(string folder)
    {
        if (!OnDemandSupported) return "„Dateien bei Bedarf“ braucht Windows 10 (Version 1809) oder neuer.";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "Der Ordner liegt im Netzwerk.";
        var root = Path.GetPathRoot(full) ?? "";
        var drive = root.TrimEnd('\\');
        if (full.Length <= drive.Length) return "Das wäre ein ganzes Laufwerk – bitte wähle einen Ordner darauf.";
        try
        {
            var info = new DriveInfo(root);
            if (!info.IsReady) return $"Laufwerk {drive} ist nicht bereit.";
            if (info.DriveType != DriveType.Fixed) return $"Laufwerk {drive} ist kein fest eingebautes Laufwerk dieses PCs.";
            if (!string.Equals(info.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) return $"Laufwerk {drive} nutzt {info.DriveFormat} statt NTFS.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Laufwerk {drive} lässt sich nicht lesen.";
        }
        // The nearest folder that exists tells whether the place lies inside another sync root.
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current)) continue;
            if (SyncRoots.ContextOf(current) is not null)
                return "Der Ordner liegt in einem Ordner, den schon ein anderes Cloud-Programm (z. B. OneDrive) oder eine andere Synchronisation nutzt.";
            break;
        }
        // Windows nests no sync roots: one below the folder rules it out, too.
        if (SyncRoots.RegisteredFolders().Any(folder => Path.TrimEndingDirectorySeparator(folder).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return "Im Ordner liegt schon ein Ordner, den ein anderes Cloud-Programm (z. B. OneDrive) oder eine andere Synchronisation nutzt.";
        return null;
    }

    internal Task<SyncRunOutcome> RunOnDemandAsync(SyncPairSettings pair, AccountSettings account, BisyncMode mode, Action<JobProgress> progress, bool keepTrash, CancellationToken cancellationToken,
        BisyncRecord? converting = null, Action<string>? activity = null)
    {
        if (!OnDemandSupported)
            return Task.FromResult(new SyncRunOutcome(false, "CD-4601", "Windows 10 1809 or later is needed", SyncDecision.None, JobProgress.None, 0, [], false));
        var runner = _onDemandRunner ??= new OnDemandRunner(_paths, _engine, _files);
        var freeUpDays = _settings.Current.Preferences.FreeUpAfterDays;
        return runner.RunAsync(LiveFor(pair.Id), pair, account, mode, progress, keepTrash, freeUpDays, Time.GetUtcNow().UtcDateTime, cancellationToken, converting, activity);
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private OnDemandPair LiveFor(string id)
    {
        lock (_gate)
        {
            _live ??= new Dictionary<string, OnDemandPair>(StringComparer.OrdinalIgnoreCase);
            if (!_live.TryGetValue(id, out var live))
            {
                live = new OnDemandPair(_paths, id, _files, () => FindPair(id), () => FindPair(id) is { } pair ? _accounts.Find(pair.AccountId) : null,
                    // The cloud has another version than a placeholder: a run brings it soon.
                    () => Worker(id)?.Request(BisyncMode.Normal));
                _live[id] = live;
            }
            return live;
        }
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private void ConnectEarly(string id)
    {
        try
        {
            LiveFor(id).EnsureConnected();
        }
        catch (CdException e)
        {
            // The first run tries again and reports what is wrong.
            Log.Warn("OnDemand", $"'{id}' not connected yet: {e.Code} {e.Detail}");
        }
    }

    /// <summary>A new registration: left-overs of an earlier synchronisation in the folder are cleared first.</summary>
    [SupportedOSPlatform("windows10.0.17763")]
    private void ConnectNew(string id)
    {
        if (FindPair(id)?.LocalPath is { } folder)
        {
            FinishCleanUps(folder);
            if (Leftovers.Count(folder) > 0) throw new Errors.CdException("CD-4602", "placeholders of an earlier synchronisation are still in the folder");
        }
        LiveFor(id).EnsureConnected();
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private void RenameInExplorer(string id)
    {
        try
        {
            LiveFor(id).Rename();
        }
        catch (Exception e) when (e is CdException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // The entry keeps its old name; nothing else depends on it.
            Log.Warn("OnDemand", $"Name in Explorer of '{id}' not changed: {e.Message}");
        }
    }

    /// <summary>
    /// Ends a synchronisation with files on demand: the placeholders are cleared (files with their data on the PC stay
    /// as normal files, online-only ones leave the PC - their data stays in the cloud), then the registration with
    /// Windows ends. Should a program hold a placeholder, the registration stays - so nothing is left that Windows calls
    /// damaged - and the folder is noted and cleared as soon as it is free (<see cref="FinishCleanUps"/>). False then.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763")]
    private bool EndOnDemand(string id)
    {
        OnDemandPair? live = null;
        lock (_gate) _live?.Remove(id, out live);
        live ??= new OnDemandPair(_paths, id, _files, () => FindPair(id), () => null, () => { });
        var folder = FindPair(id)?.LocalPath;
        var registration = live.SyncRootId;
        var ended = false;
        try
        {
            ended = live.Unregister(CleanUpWait);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn("OnDemand", $"Registration of '{id}' not ended: {e.Message}");
        }
        finally
        {
            live.Dispose();
        }
        // The registration that ended may be the one older left-overs belong to: they can be cleared now.
        FinishCleanUps(ending: id);
        if (!ended && folder is not null)
        {
            RememberCleanUp(new PendingCleanUp(registration, folder, id));
            Log.Warn("OnDemand", $"'{id}': {Leftovers.Count(folder)} placeholder(s) held by a program - the folder is cleared as soon as they are free.");
        }
        return ended || folder is null;
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private void DisconnectAll()
    {
        List<OnDemandPair> all;
        lock (_gate)
        {
            all = _live?.Values.ToList() ?? [];
            _live?.Clear();
        }
        foreach (var live in all) live.Dispose();
    }
}
