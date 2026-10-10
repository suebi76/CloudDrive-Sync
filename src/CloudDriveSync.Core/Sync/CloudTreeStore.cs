using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.OnDemand;

namespace CloudDriveSync.Core.Sync;

/// <summary>
/// The last listing of a synchronisation's cloud folder (<see cref="CloudTree"/>), kept between runs in
/// <c>sync\&lt;id&gt;\cloud-tree.json.gz</c>, so the next run reads only what may have changed. It counts only for the
/// filter rules it was listed with; anything unreadable or of another version is as if there were none - the next run
/// then reads everything, which is always right.
/// </summary>
internal sealed class CloudTreeStore(string pairFolder)
{
    private const int Version = 1;

    private string File => Path.Combine(pairFolder, "cloud-tree.json.gz");

    /// <summary>A kept listing: the tree, and when the cloud was last read completely.</summary>
    public sealed record Saved(CloudTree Tree, DateTime FullUtc);

    public Saved? Load(string filters)
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            using var stream = new GZipStream(System.IO.File.OpenRead(File), CompressionMode.Decompress);
            var data = JsonSerializer.Deserialize<Data>(stream);
            if (data is null || data.Version != Version || data.Filters != Hash(filters)) return null;
            var folders = data.Folders.ToDictionary(f => f.Key, f => (IReadOnlyList<CloudEntry>)f.Value.Select(e => new CloudEntry(e.P, e.D, e.S, e.T, e.H)).ToList(),
                StringComparer.Ordinal);
            var tree = new CloudTree(folders, data.Newest, data.Since, data.Settled.ToHashSet(StringComparer.Ordinal), data.RootTicks);
            return new Saved(tree, data.FullUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.Warn("Sync", $"Last listing of the cloud unreadable, everything is read: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Keeps the listing for the next run. The folders on the way to everything this run changed in the cloud are left
    /// out: they are read again, whatever their time says.
    /// </summary>
    public void Save(string filters, CloudTree tree, DateTime fullUtc, IEnumerable<string> changed)
    {
        var folders = new Dictionary<string, IReadOnlyList<CloudEntry>>(tree.Folders, StringComparer.Ordinal);
        foreach (var path in changed)
            for (var folder = Parent(path); ; folder = Parent(folder))
            {
                folders.Remove(folder);
                if (folder.Length == 0) break;
            }
        var data = new Data
        {
            Version = Version,
            Filters = Hash(filters),
            FullUtc = fullUtc,
            Newest = tree.Newest,
            RootTicks = tree.RootTicks,
            Folders = folders.ToDictionary(f => f.Key, f => f.Value.Select(e => new Item(e.Path, e.IsDirectory, e.Size, e.Ticks, e.Hash)).ToList()),
            Since = tree.Since.Where(s => folders.ContainsKey(s.Key)).ToDictionary(),
            Settled = tree.Settled.Where(folders.ContainsKey).ToList(),
        };
        Directory.CreateDirectory(pairFolder);
        var temporary = File + ".tmp";
        try
        {
            using (var stream = new GZipStream(System.IO.File.Create(temporary), CompressionLevel.Fastest))
                JsonSerializer.Serialize(stream, data);
            System.IO.File.Move(temporary, File, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without it the next run reads everything - slower, never wrong.
            Log.Warn("Sync", $"Listing of the cloud not kept: {e.Message}");
            Forget();
        }
    }

    /// <summary>The next run reads the whole cloud folder.</summary>
    public void Forget()
    {
        try
        {
            System.IO.File.Delete(File);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Sync", $"Last listing of the cloud not removed: {e.Message}");
        }
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var at and >= 0 ? path[..at] : "";

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Data
    {
        public int Version { get; set; }
        public string Filters { get; set; } = "";
        public DateTime FullUtc { get; set; }
        public long Newest { get; set; }
        public long RootTicks { get; set; }
        public Dictionary<string, List<Item>> Folders { get; set; } = [];
        public Dictionary<string, DateTime> Since { get; set; } = [];
        public List<string> Settled { get; set; } = [];
    }

    /// <summary>One entry, with short names - a large cloud folder has tens of thousands.</summary>
    private sealed record Item(string P, bool D, long S, long T, string? H);
}
