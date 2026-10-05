using System.Text;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// The selection of a synchronisation as an rclone filter file. Folders in the selection end with "/", files do
/// not. The file is always written completely new; bisync notices any change (its checksum) and then insists on a
/// rebuild, so hidden files are never mistaken for deleted ones.
/// </summary>
public static class SyncFilters
{
    public const string SentinelFile = ".clouddrive-sync";
    public const string TrashFolder = ".clouddrive-papierkorb";

    /// <summary>Never synchronised: CloudDrive-Sync's recycle bin, lock and temporary files of Office and other programs.</summary>
    public static IReadOnlyList<string> StandardExcludes { get; } =
    [
        $"/{TrashFolder}/**",
        "~$*",
        ".~lock.*#",
        "Thumbs.db",
        "desktop.ini",
        "*.tmp",
        "*.partial",
    ];

    public static string Build(SyncSelection selection)
    {
        var text = new StringBuilder();
        text.AppendLine("# CloudDrive-Sync - automatisch erzeugt, nicht von Hand ändern");
        foreach (var pattern in StandardExcludes) text.AppendLine("- " + pattern);
        foreach (var pattern in selection.Exclude.Where(p => !string.IsNullOrWhiteSpace(p)))
            text.AppendLine("- " + pattern.Trim());
        text.AppendLine($"+ /{SentinelFile}");
        if (selection.Mode == SelectionMode.Selected)
        {
            foreach (var path in Normalise(selection.Include))
                text.AppendLine(path.EndsWith('/') ? $"+ /{EscapeGlob(path.TrimEnd('/'))}/**" : $"+ /{EscapeGlob(path)}");
            text.AppendLine("- **");
        }
        return text.ToString();
    }

    /// <summary>Selected paths with "/" separators, without duplicates and without entries inside chosen folders.</summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string> paths)
    {
        var cleaned = paths
            .Select(p => p.Replace('\\', '/').Trim())
            .Where(p => p.Trim('/').Length > 0)
            .Select(p => p.TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var folders = cleaned.Where(p => p.EndsWith('/')).ToList();
        return cleaned
            .Where(p => !folders.Any(f => !f.Equals(p, StringComparison.OrdinalIgnoreCase) && p.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Escapes the characters rclone's filter patterns treat specially.</summary>
    public static string EscapeGlob(string path)
    {
        var text = new StringBuilder(path.Length);
        foreach (var character in path)
        {
            if (character is '*' or '?' or '[' or ']' or '{' or '}' or '\\') text.Append('\\');
            text.Append(character);
        }
        return text.ToString();
    }
}
