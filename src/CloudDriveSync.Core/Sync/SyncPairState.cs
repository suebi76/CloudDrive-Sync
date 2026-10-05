using System.Text.Json;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

public enum SyncStatus
{
    /// <summary>In step; waits for the next change or interval.</summary>
    Idle,
    /// <summary>A run is due and waits for a free slot.</summary>
    Waiting,
    Syncing,
    Paused,
    /// <summary>The last run failed; the next run tries again by itself.</summary>
    Error,
    /// <summary>Stopped until the user decides (too many deletions, rebuild, sign-in, missing folder).</summary>
    NeedsAttention,
}

/// <summary>What the user is asked to decide when a synchronisation needs attention.</summary>
public enum SyncDecision
{
    None,
    /// <summary>More deletions than allowed: apply them or restore the files.</summary>
    Deletions,
    /// <summary>The synchronisation must be rebuilt (nothing is deleted).</summary>
    Rebuild,
    /// <summary>The account must be signed in again.</summary>
    SignIn,
    /// <summary>The local folder or the sentinel file is missing.</summary>
    Folder,
}

/// <summary>The current state of one synchronisation, for the user interface. Replaced as a whole on every change.</summary>
public sealed record SyncPairState(
    string Id,
    SyncStatus Status,
    string Activity,
    JobProgress Progress,
    DateTimeOffset? LastRun,
    DateTimeOffset? LastSuccess,
    string? ErrorCode,
    string? ErrorDetail,
    SyncDecision Decision,
    IReadOnlyList<string> Conflicts,
    bool FirstSyncDone)
{
    public static SyncPairState Initial(string id, PersistedSyncState saved, bool paused) => new(
        id, paused ? SyncStatus.Paused : saved.Decision != SyncDecision.None ? SyncStatus.NeedsAttention : SyncStatus.Idle,
        "", JobProgress.None, saved.LastRun, saved.LastSuccess, saved.ErrorCode, saved.ErrorDetail, saved.Decision, [], saved.FirstSyncDone);
}

/// <summary>The part of the state that survives a restart (sync\&lt;id&gt;\state.json).</summary>
public sealed class PersistedSyncState
{
    public bool FirstSyncDone { get; set; }
    public DateTimeOffset? LastRun { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public SyncDecision Decision { get; set; }

    public static PersistedSyncState Load(string file)
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<PersistedSyncState>(File.ReadAllText(file), SettingsStore.JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            // A damaged state file only costs the remembered times.
        }
        return new PersistedSyncState();
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SettingsStore.JsonOptions));
        File.Move(temporary, file, overwrite: true);
    }
}

/// <summary>One finished run, for the activity list (sync\&lt;id&gt;\runs.jsonl).</summary>
public sealed record SyncRunRecord(
    DateTimeOffset Started,
    TimeSpan Duration,
    bool Success,
    string Kind,
    long Transfers,
    long Bytes,
    long Deletes,
    int Conflicts,
    string? ErrorCode,
    string? ErrorDetail);
