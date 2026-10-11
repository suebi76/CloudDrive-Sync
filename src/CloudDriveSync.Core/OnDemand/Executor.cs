using System.Runtime.Versioning;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.CloudFiles;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>What carrying out a plan did, for the run's result and the activity list.</summary>
/// <param name="CloudChanged">Steps left alone because the cloud no longer had the version the plan was made with.</param>
/// <param name="ServerLocked">Files someone has open in the cloud ("423 Locked"): tried again later.</param>
internal sealed record ExecutionResult(
    IReadOnlyList<FileChange> Changes,
    long Transfers,
    long Bytes,
    int Deletes,
    IReadOnlyList<string> LocalOnlyAdded,
    IReadOnlyList<string> Locked,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Broken,
    int HeldBack,
    int CloudChanged,
    IReadOnlyList<string> ServerLocked);

/// <summary>
/// Carries out a <see cref="SyncPlan"/>, step by step, each step recorded in the <see cref="ItemStore"/> as soon as it
/// is done - a run that breaks off continues where it stopped. Before a step changes anything it looks again: an upload
/// or a deletion in the cloud happens only while the cloud still has the known version, a deletion on the PC only while
/// the file is unchanged there. A step whose side changed meanwhile is left for the next run, which then sees the
/// change. Order: moves, folders, uploads, conflicts, new placeholders, refreshed ones, deletions (files before their
/// folders, which go only when empty). What the server does not let be changed (e.g. a share to read only) is never lost
/// and never tried again in every run: what may not be deleted or moved comes back from the server, a change of one's own
/// stays on the PC beside the server's version, and what may not be added stays on the PC.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed partial class Executor
{
    private readonly RcClient _rc;
    private readonly FileServer _files;
    private readonly ItemStore _store;
    private readonly SyncPairSettings _pair;
    private readonly AccountSettings _account;
    private readonly bool _keepTrash;
    private readonly Action<JobProgress>? _progress;
    private readonly CancellationToken _cancel;
    private readonly string _cloudFs;
    private readonly string _remote;
    private readonly bool _withHashes;
    private readonly HashSet<string> _takenNames;
    private readonly List<FileChange> _changes = [];
    private readonly List<string> _localOnly = [];
    private readonly List<string> _locked = [];
    private readonly List<string> _failed = [];
    private readonly List<string> _toFetch = [];
    private readonly List<string> _broken = [];
    private readonly List<string> _serverLocked = [];
    // New folders the server did not let be made: nothing below them is tried in this run.
    private readonly List<string> _refusedFolders = [];
    // New places of moves the server did not allow: the plan's further steps there are left to the next run.
    private readonly List<string> _undone = [];
    private readonly Func<string, bool> _isBroken;
    private bool _placeholdersStopped;
    private int _heldBack;
    private int _cloudChanged;
    private string? _trashStamp;
    private long _transfers;
    private long _bytes;
    // How far the run is, for its card: steps done of all, the bytes of the uploads, what is being done right now.
    private int _total;
    private int _done;
    private long _totalBytes;
    private long _currentBytes;
    private string? _current;
    private long _lastReport;
    private readonly System.Diagnostics.Stopwatch _uploading = new();
    private int _deletes;

    /// <param name="placeholdersStopped">No new placeholders: Windows wrote some broken before (<see cref="PlaceholderAlarm"/>).</param>
    /// <param name="isBroken">Whether Windows wrote a new placeholder broken; tests stand in for Windows.</param>
    public Executor(RcClient rc, FileServer files, ItemStore store, SyncPairSettings pair, AccountSettings account, bool keepTrash,
        IEnumerable<string> cloudPaths, Action<JobProgress>? progress, CancellationToken cancel, bool placeholdersStopped = false, Func<string, bool>? isBroken = null)
    {
        _placeholdersStopped = placeholdersStopped;
        _isBroken = isBroken ?? Placeholders.IsBrokenAt;
        _rc = rc;
        _files = files;
        _store = store;
        _pair = pair;
        _account = account;
        _keepTrash = keepTrash;
        _progress = progress;
        _cancel = cancel;
        _cloudFs = BisyncCommand.CloudPath(pair);
        _remote = AccountService.RemoteName(pair.AccountId) + ":";
        _withHashes = account.Kind == WebDavKind.Nextcloud;
        _takenNames = new HashSet<string>(cloudPaths, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ExecutionResult> RunAsync(SyncPlan plan)
    {
        var actions = plan.Actions;
        _total = actions.Count;
        _totalBytes = actions.OfType<Upload>().Sum(u => u.Local.Size);
        foreach (var move in actions.OfType<MoveCloud>()) await StepAsync(move.NewPath, () => MoveCloudAsync(move), "Verschiebt in der Cloud");
        foreach (var folder in actions.OfType<CreateCloudFolder>().OrderBy(a => Depth(a.Path))) await StepAsync(folder.Path, () => CreateCloudFolderAsync(folder), "Legt in der Cloud an");
        foreach (var adopt in actions.OfType<Adopt>().OrderBy(a => a.Cloud.IsDirectory ? 0 : 1).ThenBy(a => Depth(a.Path))) await StepAsync(adopt.Path, () => AdoptAsync(adopt), "Vergleicht");
        foreach (var upload in actions.OfType<Upload>()) await StepAsync(upload.Path, () => UploadAsync(upload.Local, upload.Item), Uploading);
        foreach (var mark in actions.OfType<MarkInSync>()) await StepAsync(mark.Path, () => MarkAsync(mark));
        foreach (var conflict in actions.OfType<Conflict>()) await StepAsync(conflict.Path, () => ResolveAsync(conflict), "Behält beide Fassungen");
        await CreatePlaceholdersAsync(actions.OfType<CreatePlaceholder>().Select(a => a.Cloud).ToList());
        foreach (var refresh in actions.OfType<RefreshPlaceholder>()) await StepAsync(refresh.Path, () => RefreshAsync(refresh), "Aktualisiert");
        // After the new folders, before the old ones go (only when empty).
        foreach (var move in actions.OfType<MoveLocal>()) await StepAsync(move.Path, () => MoveLocalAsync(move), "Verschiebt am PC");
        foreach (var remove in actions.OfType<RemoveLocal>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Path)))
            await StepAsync(remove.Path, () => RemoveLocalAsync(remove), "Entfernt vom PC");
        foreach (var remove in actions.OfType<RemoveCloud>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Path)))
            await StepAsync(remove.Path, () => RemoveCloudAsync(remove), "Löscht in der Cloud");
        foreach (var drop in actions.OfType<DropPlaceholder>().OrderByDescending(a => Depth(a.Path))) await StepAsync(drop.Path, () => DropAsync(drop));
        foreach (var forget in actions.OfType<Forget>().OrderBy(a => a.Item.IsDirectory ? 1 : 0).ThenByDescending(a => Depth(a.Item.Path)))
            await StepAsync(forget.Item.Path, () => ForgetAsync(forget));
        // Files that were on the PC before their new version arrived come again - after everything else.
        _total += _toFetch.Count;
        foreach (var path in _toFetch) await StepAsync(path, () => FetchAsync(path), Downloading);
        Report(force: true);
        return new ExecutionResult(_changes, _transfers, _bytes, _deletes, _localOnly, _locked, _failed, _broken, _heldBack, _cloudChanged, _serverLocked);
    }

    /// <summary>
    /// One step. What only concerns this file is noted and the run goes on; trouble with the connection or the sign-in
    /// ends the run (the next one picks up from the recorded state).
    /// </summary>
    /// <param name="doing">What the card shows meanwhile, with the file's name; null for quick steps.</param>
    private async Task StepAsync(string path, Func<Task> step, string? doing = null)
    {
        _cancel.ThrowIfCancellationRequested();
        if (Undone(path))
        {
            _done++;
            return;
        }
        if (doing is not null)
        {
            _current = $"{doing}: {Name(path)}";
            // A transfer can take a while: its name shows at once. Other steps (thousands of deletions) are summed up.
            Report(force: doing is Uploading or Downloading);
        }
        try
        {
            await step();
        }
        catch (CdException e) when (e.Code is "CD-5001" or "CD-5003" or "CD-3012" or "CD-3006")
        {
            throw;
        }
        catch (CdException e) when (e.Code == "CD-4511")
        {
            Log.Info("OnDemand", $"'{_pair.Id}': the server did not take '{path}'; it stays on the PC.");
            KeepOnPc(path);
        }
        catch (CdException e) when (e.Code == "CD-4514")
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{path}' is locked in the cloud (someone has it open); it follows later.");
            _serverLocked.Add(path);
        }
        catch (Exception e) when (IsInUse(e))
        {
            Log.Info("OnDemand", $"'{_pair.Id}': '{path}' is open in another program; it follows later.");
            _locked.Add(path);
        }
        catch (Exception e) when (e is CdException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("OnDemand", $"'{_pair.Id}': '{path}' left for the next run: {(e as CdException)?.Detail ?? e.Message}");
            _failed.Add(path);
        }
        finally
        {
            _done++;
            _current = null;
            _currentBytes = 0;
            Report(force: false);
        }
    }

    private const string Uploading = "Lädt hoch", Downloading = "Lädt herunter";

    /// <summary>Tells the card how far the run is - at most four times a second, unless a transfer begins.</summary>
    private void Report(bool force)
    {
        if (_progress is null) return;
        var now = Environment.TickCount64;
        if (!force && now - _lastReport < 250) return;
        _lastReport = now;
        var seconds = _uploading.Elapsed.TotalSeconds;
        var speed = seconds >= 1 ? (_bytes + _currentBytes) / seconds : 0;
        _progress(new JobProgress(_bytes + _currentBytes, _totalBytes, Math.Min(_done, _total), _total, speed, 0, 0, _failed.Count, Current: _current));
    }

    private string LocalFull(string path) => NameEncoding.ToLocalFullPath(_pair.LocalPath, path);

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static int Depth(string path) => path.Count(c => c == '/');

    /// <summary>A time for Windows: the server's, or now when the server gave none (FILETIME starts in 1601).</summary>
    private static DateTime TimeOf(long ticks) => ticks > new DateTime(1601, 1, 2).Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UtcNow;

    private static bool IsInUse(Exception e) =>
        (e is IOException io && io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) ||
        (e is CdException cd && cd.Code == "CD-4510");
}
