using System.Text.RegularExpressions;

namespace CloudDriveSync.Core.Diagnostics;

/// <summary>
/// Application log: one text file per day in the log folder, lines with time, level and component. Secrets are
/// redacted before anything is written. Logging never throws.
/// </summary>
public static partial class Log
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static void Initialize(string directory)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
        CleanUp(directory, TimeSpan.FromDays(14));
    }

    public static void Debug(string component, string message) => Write("DEBUG", component, message);
    public static void Info(string component, string message) => Write("INFO", component, message);
    public static void Warn(string component, string message) => Write("WARN", component, message);
    public static void Error(string component, string message) => Write("ERROR", component, message);

    private static void Write(string level, string component, string message)
    {
        var directory = _directory;
        if (directory is null) return;
        var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{level,-5}] [{component}] {Redact(message)}{Environment.NewLine}";
        try
        {
            lock (Gate) File.AppendAllText(Path.Combine(directory, $"clouddrive-sync-{DateTime.Now:yyyy-MM-dd}.log"), line);
        }
        catch (IOException)
        {
            // A log line is never worth an error.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Hides passwords, tokens and authorisation headers in a text.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = BearerPattern().Replace(text, "Bearer ***");
        text = BasicPattern().Replace(text, "Basic ***");
        text = JsonSecretPattern().Replace(text, m => m.Groups[1].Value + "\"***\"");
        return KeyValueSecretPattern().Replace(text, m => m.Groups[1].Value + "***");
    }

    private static void CleanUp(string directory, TimeSpan keep)
    {
        try
        {
            // "clouddrives-*.log": the name versions up to 0.2.0-preview.2 used; those files expire as well.
            foreach (var pattern in new[] { "clouddrive-sync-*.log", "clouddrives-*.log" })
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                    if (File.GetLastWriteTime(file) < DateTime.Now - keep) File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"(?i)Bearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)Basic\s+[A-Za-z0-9+/=]{8,}")]
    private static partial Regex BasicPattern();

    [GeneratedRegex(@"(?i)(""(?:access_token|refresh_token|token|pass|password|apppassword|appPassword|secret|client_secret)""\s*:\s*)""[^""]*""")]
    private static partial Regex JsonSecretPattern();

    [GeneratedRegex(@"(?i)\b((?:pass|password|token|secret|apppassword)\s*[=:]\s*)[^\s,;&""]+")]
    private static partial Regex KeyValueSecretPattern();
}
