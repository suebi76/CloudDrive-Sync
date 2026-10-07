using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Engine;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.OnDemand;

namespace CloudDriveSync.Core.Sync;

/// <summary>How far a listing of the cloud got: folders read, files found and their bytes.</summary>
public readonly record struct ListingProgress(int Folders, long Files, long Bytes);

/// <summary>A listing of the cloud (see <see cref="CloudWalker"/>).</summary>
/// <param name="Unreadable">Folders the server did not let be read; what is in them is unknown.</param>
internal sealed record CloudWalk(IReadOnlyList<CloudEntry> Entries, IReadOnlyList<string> Unreadable, int Folders);

/// <summary>
/// Lists a cloud folder folder by folder, eight at once. rclone's recursive listing ends at the first folder the server
/// refuses (and does not name it), and its WebDAV backend waits 10 ms between two requests. Here such a folder stays out
/// and the log names it, the pause shrinks to 1 ms - and the walk tells how far it got while it goes, so a large tree
/// never looks stuck.
/// </summary>
internal static class CloudWalker
{
    /// <summary>Folders read at once - rclone's own default for walking a tree; more hardly helps and burdens the server.</summary>
    private const int ParallelFolders = 8;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(300);

    /// <param name="fs">The cloud folder as rclone names it ("remote:path").</param>
    /// <param name="filtersFile">rclone's filter rules for the walk (selection, exclusions), or null for everything.</param>
    /// <param name="name">Names the walk in the log (the synchronisation).</param>
    public static async Task<CloudWalk> WalkAsync(RcClient rc, string fs, string? filtersFile, bool withHashes, Action<ListingProgress>? progress, string name,
        CancellationToken cancellationToken)
    {
        fs = WithoutPause(fs);
        var options = new JsonObject { ["recurse"] = false, ["noMimeType"] = true };
        if (withHashes) options["showHash"] = true;
        var listed = new ConcurrentBag<CloudEntry>();
        var unreadable = new ConcurrentBag<string>();
        int folders = 0;
        long files = 0, bytes = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var reportGate = new Lock();
        using var gate = new SemaphoreSlim(ParallelFolders);

        void Report(bool final)
        {
            if (progress is null) return;
            lock (reportGate)
            {
                if (!final && clock.Elapsed - lastReport < ReportEvery) return;
                lastReport = clock.Elapsed;
            }
            progress(new ListingProgress(Volatile.Read(ref folders), Interlocked.Read(ref files), Interlocked.Read(ref bytes)));
        }

        async Task WalkFolderAsync(string folder)
        {
            List<CloudEntry> entries;
            await gate.WaitAsync(cancellationToken);
            try
            {
                entries = await ListAsync(rc, fs, folder, filtersFile, options, cancellationToken);
            }
            catch (CdException e) when (folder.Length > 0 && !EndsTheRun(e))
            {
                unreadable.Add(folder);
                Log.Warn("Sync", $"'{name}': folder '{folder}' could not be read ({e.Code}: {Shorten(e.Detail ?? e.Message)}); it is left out.");
                return;
            }
            finally
            {
                gate.Release();
            }
            var below = new List<Task>();
            foreach (var entry in entries)
            {
                listed.Add(entry);
                if (entry.IsDirectory)
                {
                    below.Add(WalkFolderAsync(entry.Path));
                    continue;
                }
                Interlocked.Increment(ref files);
                Interlocked.Add(ref bytes, entry.Size);
            }
            Interlocked.Increment(ref folders);
            Report(final: false);
            await Task.WhenAll(below);
        }

        await WalkFolderAsync("");
        Report(final: true);
        return new CloudWalk(listed.ToList(), unreadable.Order(StringComparer.Ordinal).ToList(), folders);
    }

    /// <summary>
    /// rclone's WebDAV backend waits at least 10 ms between two requests (pacer_min_sleep): at most 100 folders a second,
    /// however fast the server. The walk limits itself to a few requests at once, so the pause shrinks to 1 ms - not to
    /// nothing, because rclone doubles it after an answer like "too many requests".
    /// </summary>
    private static string WithoutPause(string fs)
    {
        var colon = fs.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? fs : $"{fs[..colon]},pacer_min_sleep=1ms{fs[colon..]}";
    }

    /// <summary>No connection, no sign-in, no engine: the whole walk ends, as everywhere else.</summary>
    private static bool EndsTheRun(CdException e) => e.Code is "CD-5001" or "CD-5003" or "CD-3012" or "CD-3006";

    /// <summary>Servers answer a refused folder with a whole web page; the log needs its first line and the status at its end.</summary>
    private static string Shorten(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "";
        var first = lines[0].Length > 120 ? lines[0][..120] + "…" : lines[0];
        if (lines.Length == 1) return first;
        var last = lines[^1];
        var status = last.LastIndexOf(": ", StringComparison.Ordinal) is var at and >= 0 ? last[(at + 2)..] : last;
        return $"{first} … {(status.Length > 60 ? status[..60] : status)}";
    }

    /// <param name="remote">The folder to list, relative to <paramref name="fs"/>; the entries' paths are relative to <paramref name="fs"/>, too.</param>
    internal static async Task<List<CloudEntry>> ListAsync(RcClient rc, string fs, string remote, string? filtersFile, JsonObject options, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["fs"] = fs,
            ["remote"] = remote,
            ["opt"] = options.DeepClone(),
        };
        if (filtersFile is not null) body["_filter"] = new JsonObject { ["FilterFrom"] = new JsonArray(filtersFile) };
        var result = await rc.CallAsync("operations/list", body, TimeSpan.FromMinutes(10), cancellationToken);
        var entries = new List<CloudEntry>();
        foreach (var node in result["list"] as JsonArray ?? [])
        {
            if (node is not JsonObject item) continue;
            var path = item["Path"]?.GetValue<string>() ?? "";
            if (path.Length == 0) continue;
            var isDirectory = item["IsDir"]?.GetValue<bool>() == true;
            var size = isDirectory ? 0 : item["Size"] is JsonValue s && s.TryGetValue<long>(out var length) ? length : 0;
            var ticks = DateTimeOffset.TryParse(item["ModTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var modified)
                ? modified.UtcTicks : 0;
            entries.Add(new CloudEntry(path, isDirectory, size, ticks, FileServer.HashOf(item)));
        }
        return entries;
    }
}
