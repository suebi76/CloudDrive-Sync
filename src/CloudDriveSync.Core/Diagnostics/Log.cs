using System.Text.RegularExpressions;

namespace CloudDriveSync.Core.Diagnostics;

/// <summary>
/// Application log: one text file per day in the log folder, lines with time, level and component. Secrets are
/// redacted before anything is written. Logging never throws. Logs older than <see cref="Keep"/> go - CloudDrive-Sync's
/// and rclone's closed parts - at the start and at every new day, so also while CloudDrive-Sync runs for weeks.
/// </summary>
public static partial class Log
{
    /// <summary>How long logs are kept.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(14);

    private static readonly object Gate = new();
    private static string? _directory;
    private static string? _file;
    private static readonly AsyncLocal<List<string>?> Captured = new();

    /// <summary>The clock for the names of the files and their age; tests set another one.</summary>
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    public static void Initialize(string directory)
    {
        Directory.CreateDirectory(directory);
        lock (Gate)
        {
            _directory = directory;
            _file = null;
        }
        CleanUp(directory, Keep);
    }

    public static void Debug(string component, string message) => Write("DEBUG", component, message);
    public static void Info(string component, string message) => Write("INFO", component, message);
    public static void Warn(string component, string message) => Write("WARN", component, message);
    public static void Error(string component, string message) => Write("ERROR", component, message);

    /// <summary>
    /// For tests: what the calling test writes from now on (its own flow and the tasks it starts) is kept in the list,
    /// too. Tests running beside it write in flows of their own and do not reach it.
    /// </summary>
    internal static List<string> Capture()
    {
        var lines = new List<string>();
        Captured.Value = lines;
        return lines;
    }

    private static void Write(string level, string component, string message)
    {
        if (Captured.Value is { } captured)
            lock (captured) captured.Add($"[{level}] [{component}] {Redact(message)}");
        var directory = _directory;
        if (directory is null) return;
        var now = Clock.GetLocalNow();
        var line = $"{now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{level,-5}] [{component}] {Redact(message)}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(directory, $"clouddrive-sync-{now:yyyy-MM-dd}.log");
                // A new day: what is older than the time logs are kept goes.
                if (_file is not null && file != _file) CleanUp(directory, Keep);
                _file = file;
                File.AppendAllText(file, line);
            }
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
            // "clouddrives-*.log": the name versions up to 0.2.0-preview.2 used; those files expire as well. "rclone-*.log":
            // the closed parts of rclone's log (rclone.log itself is the engine's open part, see RcloneEngine).
            var before = Clock.GetLocalNow().DateTime - keep;
            foreach (var pattern in new[] { "clouddrive-sync-*.log", "clouddrives-*.log", "rclone-*.log" })
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                    if (File.GetLastWriteTime(file) < before) File.Delete(file);
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
