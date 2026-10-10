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
/// <param name="Tree">What the walk saw, for the next one.</param>
/// <param name="Read">Folders actually read from the server; the others came from the last walk.</param>
internal sealed record CloudWalk(IReadOnlyList<CloudEntry> Entries, IReadOnlyList<string> Unreadable, int Folders, CloudTree Tree, int Read);

/// <summary>
/// What a walk saw: every folder it covered ("" is the walked folder itself) with the entries in it. A folder's time is
/// in the entry of its parent.
/// </summary>
/// <param name="Newest">The newest folder time seen - the server's clock had reached at least that when it was read.</param>
/// <param name="Since">When (this PC's clock) each folder was first read with the time it has now.</param>
/// <param name="Settled">Folders read again at least <see cref="CloudWalker.SettleTime"/> after their time was first seen.</param>
/// <param name="RootTicks">The walked folder's own time, as its parent showed it; 0 when unknown (a whole account).</param>
internal sealed record CloudTree(IReadOnlyDictionary<string, IReadOnlyList<CloudEntry>> Folders, long Newest,
    IReadOnlyDictionary<string, DateTime> Since, IReadOnlySet<string> Settled, long RootTicks = 0);

/// <summary>Reads one folder: its entries, with paths relative to the walked folder.</summary>
internal delegate Task<List<CloudEntry>> FolderLister(string folder, CancellationToken cancellationToken);

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
    /// <param name="previous">What the last walk saw (see the other overload), or null to read everything.</param>
    public static Task<CloudWalk> WalkAsync(RcClient rc, string fs, string? filtersFile, bool withHashes, Action<ListingProgress>? progress, string name,
        CancellationToken cancellationToken, CloudTree? previous = null, long rootTicks = 0, Func<DateTime>? utcNow = null)
    {
        fs = WithoutPause(fs);
        var options = new JsonObject { ["recurse"] = false, ["noMimeType"] = true };
        if (withHashes) options["showHash"] = true;
        return WalkAsync((folder, token) => ListAsync(rc, fs, folder, filtersFile, options, token), previous, progress, name, cancellationToken, utcNow, rootTicks);
    }

    /// <summary>
    /// The time of the folder <paramref name="name"/> in the folder <paramref name="parentFs"/> ("remote:path"), as the
    /// server lists it there; 0 when the folder is not found or cannot be read.
    /// </summary>
    public static async Task<long> FolderTicksAsync(RcClient rc, string parentFs, string name, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await ListAsync(rc, WithoutPause(parentFs), "", null, new JsonObject { ["recurse"] = false, ["noMimeType"] = true, ["dirsOnly"] = true }, cancellationToken);
            return entries.FirstOrDefault(e => e.IsDirectory && e.Path == name)?.Ticks ?? 0;
        }
        catch (CdException e) when (!EndsTheRun(e))
        {
            return 0;
        }
    }

    /// <summary>
    /// How long after a folder's time was first seen it must be read again before that time is trusted: times come in
    /// whole seconds, and a change in the same second after the folder was read leaves its time as it was.
    /// </summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Walks the cloud. With <paramref name="previous"/>, a folder is read again only when it may have changed - for
    /// servers whose folder times change with anything below them (Nextcloud). The result is always what reading every
    /// folder gives. A folder's time counts only when it is the same as last time and cannot be a change in the very
    /// second the folder was read then (older than the newest time seen then, or read again a while later: "settled").
    /// A folder is taken from the last walk without reading it when
    /// <list type="bullet">
    /// <item>its parent was taken over, too - nothing below an unchanged folder changed; or</item>
    /// <item>its time counts and no other folder had that time - so no other folder can have been moved to its place.</item>
    /// </list>
    /// A folder that is read and turns out exactly as last time, with a time that counts, passes its sub-folders on as
    /// taken over: a folder shares its time with the sub-folder of its newest change, so that time is rarely unique.
    /// </summary>
    /// <param name="list">Reads one folder.</param>
    /// <param name="previous">What the last walk saw.</param>
    /// <param name="utcNow">This PC's clock; tests move it.</param>
    /// <param name="rootTicks">The walked folder's own time from its parent's listing; 0 when there is no parent to ask.</param>
    public static async Task<CloudWalk> WalkAsync(FolderLister list, CloudTree? previous, Action<ListingProgress>? progress, string name, CancellationToken cancellationToken,
        Func<DateTime>? utcNow = null, long rootTicks = 0)
    {
        var now = (utcNow ?? (() => DateTime.UtcNow))();
        var listed = new ConcurrentBag<CloudEntry>();
        var tree = new ConcurrentDictionary<string, IReadOnlyList<CloudEntry>>(StringComparer.Ordinal);
        var since = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        var settled = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var unreadable = new ConcurrentBag<string>();
        int folders = 0, read = 0;
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

        // A folder's time as the last walk saw it in its parent, and how many folders had each time.
        var knownTimes = previous?.Folders.Values.SelectMany(e => e).Where(e => e.IsDirectory).ToDictionary(e => e.Path, e => e.Ticks, StringComparer.Ordinal) ?? [];
        if (previous is { RootTicks: not 0 }) knownTimes[""] = previous.RootTicks;
        var foldersWithTime = knownTimes.Values.CountBy(t => t).ToDictionary();

        // The same time as last time, and not one that a change in the second it was read could have left as it was.
        bool TimeCounts(string folder, long ticks) =>
            previous is not null && ticks != 0 && knownTimes.TryGetValue(folder, out var known) && known == ticks && previous.Folders.ContainsKey(folder) &&
            (ticks < previous.Newest || previous.Settled.Contains(folder));

        async Task WalkFolderAsync(string folder, long ticks, bool parentKept)
        {
            IReadOnlyList<CloudEntry> entries;
            var kept = (parentKept && previous!.Folders.ContainsKey(folder)) || (TimeCounts(folder, ticks) && foldersWithTime[ticks] == 1);
            if (kept)
            {
                entries = previous!.Folders[folder];
                if (previous.Since.TryGetValue(folder, out var first)) since[folder] = first;
                if (previous.Settled.Contains(folder)) settled[folder] = true;
            }
            else
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    Interlocked.Increment(ref read);
                    entries = await list(folder, cancellationToken);
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
                // Read again with the same time a while after that time was first seen: whatever happened in its second is in.
                var sameTime = previous is not null && ticks != 0 && knownTimes.TryGetValue(folder, out var before) && before == ticks && previous.Since.ContainsKey(folder);
                since[folder] = sameTime ? previous!.Since[folder] : now;
                if (sameTime && now - since[folder] >= SettleTime) settled[folder] = true;
                // Exactly as last time, with a time that counts: nothing below it changed either.
                kept = TimeCounts(folder, ticks) && Same(entries, previous!.Folders[folder]);
            }
            tree[folder] = entries;
            var below = new List<Task>();
            foreach (var entry in entries)
            {
                listed.Add(entry);
                if (entry.IsDirectory)
                {
                    below.Add(WalkFolderAsync(entry.Path, entry.Ticks, kept));
                    continue;
                }
                Interlocked.Increment(ref files);
                Interlocked.Add(ref bytes, entry.Size);
            }
            Interlocked.Increment(ref folders);
            Report(final: false);
            await Task.WhenAll(below);
        }

        // The walked folder itself is always read; with its time from its parent, it can pass its sub-folders on.
        await WalkFolderAsync("", rootTicks, parentKept: false);
        Report(final: true);
        var all = listed.ToList();
        var newest = all.Where(e => e.IsDirectory).Select(e => e.Ticks).DefaultIfEmpty(0).Max();
        return new CloudWalk(all, unreadable.Order(StringComparer.Ordinal).ToList(), folders,
            new CloudTree(tree, newest, since, settled.Keys.ToHashSet(StringComparer.Ordinal), rootTicks), read);
    }

    /// <summary>Two listings of a folder with the same entries - names, kinds, sizes, times and checksums.</summary>
    private static bool Same(IReadOnlyList<CloudEntry> now, IReadOnlyList<CloudEntry> before) =>
        now.Count == before.Count && now.ToHashSet().SetEquals(before);

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
