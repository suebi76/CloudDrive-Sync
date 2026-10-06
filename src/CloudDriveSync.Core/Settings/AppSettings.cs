using System.Text.Json.Serialization;

namespace CloudDriveSync.Core.Settings;

/// <summary>
/// Everything CloudDrive-Sync remembers between starts - never a secret. Passwords and tokens live only in the
/// encrypted rclone configuration.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public List<AccountSettings> Accounts { get; set; } = [];
    public List<SyncPairSettings> Syncs { get; set; } = [];
    public Preferences Preferences { get; set; } = new();
}

/// <summary>Kinds of WebDAV servers CloudDrive-Sync knows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebDavKind>))]
public enum WebDavKind
{
    Nextcloud,
    IServ,
    Other,
}

/// <summary>
/// A connected cloud account. Its sign-in is not here: the password or app password lies only in the encrypted rclone
/// configuration, as the remote "cd-&lt;id&gt;" (see AccountService).
/// </summary>
public sealed class AccountSettings
{
    public string Id { get; set; } = "";
    /// <summary>Provider family; CloudDrive-Sync starts with WebDAV (Nextcloud, IServ and others).</summary>
    public string Provider { get; set; } = "webdav";
    public WebDavKind Kind { get; set; } = WebDavKind.Other;
    public string Label { get; set; } = "";
    /// <summary>Who is signed in (user at server), to recognise the same account again.</summary>
    public AccountIdentity? Identity { get; set; }
    public DateTimeOffset Added { get; set; }

    public override string ToString() => Label;
}

/// <summary>Who is signed in to an account: the user ID on the server and the name to show.</summary>
public sealed class AccountIdentity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>What a synchronisation takes from its cloud folder.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelectionMode>))]
public enum SelectionMode
{
    /// <summary>Everything below the cloud folder.</summary>
    All,
    /// <summary>Only the chosen folders and files.</summary>
    Selected,
}

/// <summary>What happens to a file that changed on both sides since the last run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictPolicy>))]
public enum ConflictPolicy
{
    /// <summary>The newer version keeps the name, the older one stays as a conflict copy.</summary>
    NewerWins,
    /// <summary>Both versions are renamed and kept.</summary>
    KeepBoth,
    /// <summary>The cloud version keeps the name, the PC version stays as a conflict copy.</summary>
    CloudWins,
    /// <summary>The PC version keeps the name, the cloud version stays as a conflict copy.</summary>
    PcWins,
}

/// <summary>How the files of a synchronisation live on the PC.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncMode>))]
public enum SyncMode
{
    /// <summary>Every file lies on the PC, kept in step by rclone's bisync - the only way on drives without NTFS.</summary>
    Classic,
    /// <summary>
    /// Files on demand: every file appears in Explorer at once, its data comes from the cloud when it is opened or kept
    /// on this device (Windows Cloud Files API).
    /// </summary>
    OnDemand,
}

/// <summary>Which folders and files of the cloud folder take part in a synchronisation.</summary>
public sealed class SyncSelection
{
    public SelectionMode Mode { get; set; } = SelectionMode.All;
    /// <summary>Folders and files (relative to the cloud folder, "/" separated) when <see cref="Mode"/> is Selected.</summary>
    public List<string> Include { get; set; } = [];
    /// <summary>Additional patterns that are never synchronised, e.g. "**/node_modules/**".</summary>
    public List<string> Exclude { get; set; } = [];
}

/// <summary>A cloud folder kept in step with a local folder, in both directions.</summary>
public sealed class SyncPairSettings
{
    public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    /// <summary>Folder in the cloud, relative to the account ("" = everything).</summary>
    public string RemotePath { get; set; } = "";
    public string LocalPath { get; set; } = "";
    /// <summary>
    /// Classic unless chosen otherwise: synchronisations from before version 0.3 have no such entry and must stay as
    /// they are. The setup preselects files on demand where the folder allows it.
    /// </summary>
    public SyncMode Mode { get; set; } = SyncMode.Classic;
    /// <summary>The name in Explorer's navigation pane; empty means "account – cloud folder".</summary>
    public string? ExplorerName { get; set; }
    public SyncSelection Selection { get; set; } = new();
    public ConflictPolicy Conflicts { get; set; } = ConflictPolicy.NewerWins;
    /// <summary>Cloud changes are fetched this often; local changes go up shortly after they happen.</summary>
    public int IntervalMinutes { get; set; } = 5;
    public bool OnLocalChange { get; set; } = true;
    /// <summary>Stop and ask when more than this share of the files would be deleted.</summary>
    public int MaxDeletePercent { get; set; } = 50;
    public bool Paused { get; set; }
    public DateTimeOffset Created { get; set; }
    /// <summary>
    /// The protection file lies in the cloud folder as well. Some folders take no files (IServ: "Groups" itself, the
    /// whole account; folders to read only) - then CloudDrive-Sync checks the cloud folder itself before each run.
    /// </summary>
    public bool CloudCheckFile { get; set; } = true;
    /// <summary>
    /// Files the server did not take (a folder to read only), relative and "/" separated. They stay on this PC and out
    /// of the synchronisation until the user tries them again.
    /// </summary>
    public List<string> LocalOnly { get; set; } = [];
}

/// <summary>Settings of the program as a whole (the page "Einstellungen").</summary>
public sealed class Preferences
{
    public bool StartWithWindows { get; set; } = true;
    public bool Notifications { get; set; } = true;
    /// <summary>Days deleted or overwritten local files stay in the sync recycle bin.</summary>
    public int TrashDays { get; set; } = 30;
    /// <summary>
    /// Files on demand: files not opened for this many days give their space back (pinned ones never); 0 = never, the
    /// default.
    /// </summary>
    public int FreeUpAfterDays { get; set; }
    /// <summary>How new versions of CloudDrive-Sync arrive.</summary>
    public UpdateMode Updates { get; set; } = UpdateMode.Notify;
    /// <summary>Also test versions (pre-releases on GitHub) - for testers.</summary>
    public bool TestVersions { get; set; }
    /// <summary>The newest version the user was told about, so the notice comes once per version.</summary>
    public string? AnnouncedUpdate { get; set; }
}

/// <summary>How new versions of CloudDrive-Sync arrive.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpdateMode>))]
public enum UpdateMode
{
    /// <summary>A notice; installed with one click.</summary>
    Notify,
    /// <summary>Downloaded and installed on their own at a quiet moment.</summary>
    Automatic,
    /// <summary>Only when the user looks for them.</summary>
    Manual,
}
