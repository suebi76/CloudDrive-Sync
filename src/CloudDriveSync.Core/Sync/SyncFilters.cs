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

    /// <summary>The filter of a synchronisation, with the files that stay on the PC.</summary>
    public static string Build(SyncPairSettings pair) => Build(pair.Selection, pair.CloudCheckFile, pair.LocalOnly);

    /// <param name="cloudCheckFile">The protection file lies in the cloud, too (see <see cref="SyncPairSettings.CloudCheckFile"/>).</param>
    /// <param name="localOnly">Files that stay on the PC (see <see cref="SyncPairSettings.LocalOnly"/>).</param>
    public static string Build(SyncSelection selection, bool cloudCheckFile = true, IEnumerable<string>? localOnly = null)
    {
        var text = new StringBuilder();
        text.AppendLine("# CloudDrive-Sync - automatisch erzeugt, nicht von Hand ändern");
        foreach (var pattern in StandardExcludes) text.AppendLine("- " + pattern);
        foreach (var pattern in selection.Exclude.Where(p => !string.IsNullOrWhiteSpace(p)))
            text.AppendLine("- " + pattern.Trim());
        // On both sides the protection file is part of the synchronisation; where only the PC has it, it stays out.
        text.AppendLine(cloudCheckFile ? $"+ /{SentinelFile}" : $"- /{SentinelFile}");
        // A file, or a folder with everything in it: "- /Ordner" alone leaves the folder and its content in (rclone).
        foreach (var path in localOnly ?? [])
        {
            text.AppendLine($"- /{EscapeGlob(path)}");
            text.AppendLine($"- /{EscapeGlob(path)}/**");
        }
        if (selection.Mode == SelectionMode.Selected)
        {
            foreach (var path in Normalise(selection.Include))
                text.AppendLine(path.EndsWith('/') ? $"+ /{EscapeGlob(path.TrimEnd('/'))}/**" : $"+ /{EscapeGlob(path)}");
            text.AppendLine("- **");
        }
        return text.ToString();
    }

    /// <summary>
    /// Writes the filter file of a synchronisation. bisync demands a rebuild whenever the file changes - right for a
    /// new selection, needless when only files that stay on the PC came or went: then bisync's checksum of the file
    /// (filter.txt.md5) is renewed, too. "filter-base.txt" keeps the filter without those files to tell both apart.
    /// </summary>
    internal static void Write(string pairFolder, string filtersFile, SyncPairSettings pair)
    {
        var baseFile = Path.Combine(pairFolder, "filter-base.txt");
        var baseContent = Build(pair.Selection, pair.CloudCheckFile);
        var content = Build(pair);
        var current = File.Exists(filtersFile) ? File.ReadAllText(filtersFile) : null;
        // Filters written before files could stay on the PC are their own base.
        var knownBase = File.Exists(baseFile) ? File.ReadAllText(baseFile) : current;
        if (current == content)
        {
            if (!File.Exists(baseFile)) File.WriteAllText(baseFile, baseContent);
            return;
        }
        File.WriteAllText(filtersFile, content);
        File.WriteAllText(baseFile, baseContent);
        var checksum = filtersFile + ".md5";
        if (knownBase == baseContent && File.Exists(checksum))
            File.WriteAllText(checksum, Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(filtersFile))));
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
    /// <summary>The rule that leaves out every file with this ending, in any case ("*.[dD][lL][lL]").</summary>
    public static string TypeRule(string extension)
    {
        var text = new StringBuilder("*.");
        foreach (var character in extension.TrimStart('.'))
        {
            if (char.IsLetter(character) && char.ToLowerInvariant(character) != char.ToUpperInvariant(character))
                text.Append('[').Append(char.ToLowerInvariant(character)).Append(char.ToUpperInvariant(character)).Append(']');
            else text.Append(EscapeGlob(character.ToString()));
        }
        return text.ToString();
    }

    /// <summary>The rule that leaves out one file or folder ("/" separated, relative to the synchronised folder).</summary>
    public static string PathRule(string path, bool folder) => folder ? $"/{EscapeGlob(path.Trim('/'))}/**" : $"/{EscapeGlob(path.Trim('/'))}";

    /// <summary>What a rule leaves out, in words - for the list of what is ignored.</summary>
    public static string Describe(string rule)
    {
        if (rule.StartsWith("*.", StringComparison.Ordinal) && UnescapeType(rule[2..]) is { } type) return $"Dateien vom Typ .{type}";
        if (rule.StartsWith('/') && rule.EndsWith("/**", StringComparison.Ordinal)) return $"Ordner „{Unescape(rule[1..^3])}“";
        if (rule.StartsWith('/') && !rule.Contains('*', StringComparison.Ordinal)) return $"Datei „{Unescape(rule[1..])}“";
        return rule;
    }

    private static string? UnescapeType(string glob)
    {
        var text = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '[' && i + 3 < glob.Length && glob[i + 3] == ']')
            {
                text.Append(glob[i + 1]);
                i += 3;
            }
            else if (glob[i] == '\\' && i + 1 < glob.Length) text.Append(glob[++i]);
            else if (glob[i] is '*' or '?' or '[' or ']' or '/') return null;
            else text.Append(glob[i]);
        }
        return text.ToString();
    }

    private static string Unescape(string glob)
    {
        var text = new StringBuilder(glob.Length);
        for (var i = 0; i < glob.Length; i++) text.Append(glob[i] == '\\' && i + 1 < glob.Length ? glob[++i] : glob[i]);
        return text.ToString();
    }

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
