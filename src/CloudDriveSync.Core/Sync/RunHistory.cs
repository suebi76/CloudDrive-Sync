using System.Text.Json;
using CloudDriveSync.Core.Settings;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// The finished runs of one synchronisation, kept in runs.jsonl in its folder: one JSON line per run, the newest last.
/// Once the file holds 400 runs, the oldest 200 are dropped.
/// </summary>
public static class RunHistory
{
    private const string FileName = "runs.jsonl";
    private static readonly JsonSerializerOptions LineOptions = new(SettingsStore.JsonOptions) { WriteIndented = false };

    /// <summary>The newest <paramref name="count"/> runs, newest first. Lines that cannot be read are skipped.</summary>
    public static IReadOnlyList<SyncRunRecord> Read(string pairFolder, int count)
    {
        var file = Path.Combine(pairFolder, FileName);
        if (!File.Exists(file)) return [];
        var records = new List<SyncRunRecord>();
        foreach (var line in File.ReadLines(file).Reverse().Take(count))
        {
            try
            {
                if (JsonSerializer.Deserialize<SyncRunRecord>(line, SettingsStore.JsonOptions) is { } record) records.Add(record);
            }
            catch (JsonException)
            {
                // A line cut off when the PC went down; the other runs still count.
            }
        }
        return records;
    }

    /// <summary>Adds a run. Throws <see cref="IOException"/> when the file cannot be written.</summary>
    public static void Append(string pairFolder, SyncRunRecord record)
    {
        var file = Path.Combine(pairFolder, FileName);
        Directory.CreateDirectory(pairFolder);
        File.AppendAllText(file, JsonSerializer.Serialize(record, LineOptions) + Environment.NewLine);
        var lines = File.ReadAllLines(file);
        if (lines.Length > 400) File.WriteAllLines(file, lines[^200..]);
    }
}
