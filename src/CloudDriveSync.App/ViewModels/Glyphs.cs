using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.App.ViewModels;

/// <summary>Symbols of the Segoe Fluent Icons font.</summary>
public static class Glyphs
{
    public const string Home = "\uE80F";
    public const string Accounts = "\uE77B";
    public const string History = "\uE81C";
    public const string Settings = "\uE713";
    public const string Cloud = "\uE753";
    public const string School = "\uE7BE";
    public const string Globe = "\uE774";
    public const string Sync = "\uE895";
    public const string Done = "\uE930";
    public const string Clock = "\uE823";
    public const string Pause = "\uE769";
    public const string Warning = "\uE7BA";
    public const string Error = "\uEA39";
    public const string Folder = "\uE8B7";
    public const string Document = "\uE8A5";
    public const string Trash = "\uE74D";

    public static string Of(WebDavKind kind) => kind switch
    {
        WebDavKind.Nextcloud => Cloud,
        WebDavKind.IServ => School,
        _ => Globe,
    };

    public static string NameOf(WebDavKind kind) => kind switch
    {
        WebDavKind.Nextcloud => "Nextcloud",
        WebDavKind.IServ => "IServ",
        _ => "WebDAV",
    };
}

/// <summary>How good or bad a state is - decides the colour.</summary>
public enum Tone
{
    Ok,
    Busy,
    Paused,
    Warning,
    Error,
    Neutral,
}
