namespace CloudDriveSync.Core.Sync;

/// <summary>What a notice is about. Conflicts come with an information symbol, everything else with a warning symbol.</summary>
public enum SyncNoticeKind
{
    Conflicts,
    NeedsAttention,
    Error,
}

/// <summary>Something the user should hear about (shown as a Windows notification).</summary>
public sealed record SyncNotice(string PairId, SyncNoticeKind Kind, string Title, string Text);
