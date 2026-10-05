using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Accounts;

/// <summary>
/// Cloud folders by the names people know: IServ's WebDAV calls its two top folders "Files" and "Groups", its web pages
/// "Eigene Dateien" and "Gruppen". Only the shown names change - paths stay as the server has them.
/// </summary>
public static class CloudFolderNames
{
    /// <summary>The name to show for a folder; <paramref name="path"/> is its path below the account.</summary>
    public static string Show(WebDavKind kind, string name, string path) =>
        kind == WebDavKind.IServ && !path.Trim('/').Contains('/')
            ? name switch
            {
                "Files" => "Eigene Dateien",
                "Groups" => "Gruppen",
                _ => name,
            }
            : name;

    /// <summary>A folder path to show, e.g. "Gruppen › 7a" for "Groups/7a" - "Alles" for the whole account.</summary>
    public static string ShowPath(WebDavKind kind, string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Alles";
        parts[0] = Show(kind, parts[0], parts[0]);
        return string.Join(" › ", parts);
    }
}