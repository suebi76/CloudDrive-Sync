using CloudDriveSync.Core.OnDemand;
using CloudDriveSync.Core.Sync;

namespace CloudDriveSync.Core.Tests;

/// <summary>
/// Reading a cloud again with what the last walk saw (<see cref="CloudWalker"/>): a folder whose time is unchanged is not
/// read again - and the result is always exactly what reading everything gives.
/// </summary>
public class CloudWalkerTests
{
    /// <summary>
    /// A cloud in memory that behaves like Nextcloud: whenever anything below a folder changes, the folder's time
    /// changes, up to the top. Counts how often a folder is read.
    /// </summary>
    private sealed class FakeCloud(Random? sameSecond = null)
    {
        private readonly SortedDictionary<string, (bool IsDirectory, long Size, long Ticks)> _items = new(StringComparer.Ordinal);
        private long _clock = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;

        public IReadOnlyList<string> Files => _items.Where(i => !i.Value.IsDirectory).Select(i => i.Key).ToList();
        public IReadOnlyList<string> FolderPaths => _items.Where(i => i.Value.IsDirectory).Select(i => i.Key).ToList();

        public int Listings { get; private set; }

        /// <summary>The time of the walked folder itself, as its parent would show it.</summary>
        public long RootTicks { get; private set; }

        /// <summary>Time passes on the server, as on the PC: the next change falls into a later second.</summary>
        public void Advance(TimeSpan time) => _clock += time.Ticks;

        public void ResetCount() => Listings = 0;

        public void Write(string path, long size)
        {
            AddFolders(Parent(path));
            _items[path] = (false, size, Tick());
            Touch(path);
        }

        public void Delete(string path)
        {
            foreach (var key in _items.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) _items.Remove(key);
            Touch(path);
        }

        /// <summary>A move keeps the times of what moves (as Nextcloud does); both parents get the time of the move.</summary>
        public void Move(string from, string to)
        {
            if (from == to || to.StartsWith(from + "/", StringComparison.Ordinal) || _items.ContainsKey(to)) return;
            AddFolders(Parent(to));
            foreach (var key in _items.Keys.Where(k => k == from || k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            {
                var item = _items[key];
                _items.Remove(key);
                _items[to + key[from.Length..]] = item;
            }
            Touch(from);
            Touch(to);
        }

        public Task<List<CloudEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        {
            Listings++;
            var entries = _items.Where(i => Parent(i.Key) == folder)
                .Select(i => new CloudEntry(i.Key, i.Value.IsDirectory, i.Value.Size, i.Value.Ticks, null)).ToList();
            return Task.FromResult(entries);
        }

        private void AddFolders(string folder)
        {
            if (folder.Length == 0 || _items.ContainsKey(folder)) return;
            AddFolders(Parent(folder));
            _items[folder] = (true, 0, Tick());
        }

        /// <summary>Nextcloud's propagation: every folder above a change gets the time of the change.</summary>
        private void Touch(string path)
        {
            var now = Tick();
            for (var folder = Parent(path); folder.Length > 0; folder = Parent(folder))
                _items[folder] = _items[folder] with { Ticks = now };
            RootTicks = now;
        }

        /// <summary>Times in whole seconds, as WebDAV gives them: several changes can fall into one second.</summary>
        private long Tick() => sameSecond is not null && sameSecond.Next(2) == 0 ? _clock : _clock += TimeSpan.TicksPerSecond;

        private static string Parent(string path) => path.LastIndexOf('/') is var at and >= 0 ? path[..at] : "";
    }

    private static FakeCloud Sample()
    {
        var cloud = new FakeCloud();
        for (var i = 0; i < 30; i++)
            for (var j = 0; j < 3; j++) cloud.Write($"Ordner {i / 10}/Unterordner {i}/Datei {j}.txt", i * 10 + j);
        cloud.Write("oben.txt", 1);
        return cloud;
    }

    private static List<CloudEntry> Sorted(IEnumerable<CloudEntry> entries) => entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task After_any_changes_reading_again_gives_exactly_what_reading_everything_gives()
    {
        for (var seed = 1; seed <= 300; seed++)
        {
            var random = new Random(seed);
            var cloud = new FakeCloud(random);
            var pc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < random.Next(1, 40); i++) cloud.Write($"F{random.Next(4)}/G{random.Next(4)}/H{random.Next(3)}/d{i}.txt", i);
            var withRoot = seed % 2 == 0;
            long Root() => withRoot ? cloud.RootTicks : 0;
            var tree = (await CloudWalker.WalkAsync(cloud.ListAsync, previous: null, progress: null, "test", CancellationToken.None, () => pc, Root())).Tree;
            for (var round = 0; round < 8; round++)
            {
                // Sometimes the next walk comes at once, sometimes a while later - on both clocks.
                if (random.Next(2) == 0)
                {
                    var pause = TimeSpan.FromSeconds(random.Next(1, 20));
                    pc += pause;
                    cloud.Advance(pause);
                }
                var changes = new List<string>();
                for (var c = 0; c < random.Next(0, 6); c++) changes.Add(Change(cloud, random));
                var again = await CloudWalker.WalkAsync(cloud.ListAsync, tree, progress: null, "test", CancellationToken.None, () => pc, Root());
                var full = await CloudWalker.WalkAsync(cloud.ListAsync, previous: null, progress: null, "test", CancellationToken.None, () => pc, Root());
                Assert.True(Sorted(full.Entries).SequenceEqual(Sorted(again.Entries)), $"seed {seed}, round {round}: {string.Join("; ", changes)}");
                tree = again.Tree;
            }
        }
    }

    /// <summary>One random change, as people make them: new, changed, deleted, moved, renamed - also two folders swapped.</summary>
    private static string Change(FakeCloud cloud, Random random)
    {
        var files = cloud.Files;
        var folders = cloud.FolderPaths;
        string Any(IReadOnlyList<string> items) => items[random.Next(items.Count)];
        switch (random.Next(7))
        {
            case 0:
                var path = $"F{random.Next(4)}/G{random.Next(4)}/n{random.Next(1000)}.txt";
                cloud.Write(path, random.Next(100));
                return $"new {path}";
            case 1 when files.Count > 0:
                var changed = Any(files);
                cloud.Write(changed, random.Next(100, 200));
                return $"changed {changed}";
            case 2 when files.Count > 0:
                var deleted = Any(files);
                cloud.Delete(deleted);
                return $"deleted {deleted}";
            case 3 when folders.Count > 0:
                var gone = Any(folders);
                cloud.Delete(gone);
                return $"deleted folder {gone}";
            case 4 when files.Count > 0:
                var file = Any(files);
                var target = $"F{random.Next(4)}/m{random.Next(1000)}.txt";
                cloud.Move(file, target);
                return $"moved {file} to {target}";
            case 5 when folders.Count > 0:
                var folder = Any(folders);
                var renamed = folder + "x";
                cloud.Move(folder, renamed);
                return $"renamed {folder} to {renamed}";
            case 6 when folders.Count > 1:
                var a = Any(folders);
                var b = Any(folders);
                if (a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal)) return "nothing";
                cloud.Move(a, a + ".tmp");
                cloud.Move(b, a);
                cloud.Move(a + ".tmp", b);
                return $"swapped {a} and {b}";
            default:
                return "nothing";
        }
    }

    /// <summary>Walks once, and once more a little later, so the folders of the newest change are settled.</summary>
    private static async Task<CloudTree> SettledAsync(FakeCloud cloud, Func<DateTime> pc, Action later, bool withRoot)
    {
        var tree = (await CloudWalker.WalkAsync(cloud.ListAsync, previous: null, progress: null, "test", CancellationToken.None, pc, withRoot ? cloud.RootTicks : 0)).Tree;
        later();
        return (await CloudWalker.WalkAsync(cloud.ListAsync, tree, progress: null, "test", CancellationToken.None, pc, withRoot ? cloud.RootTicks : 0)).Tree;
    }

    [Fact]
    public async Task An_unchanged_folder_needs_no_listing_besides_its_parents()
    {
        var cloud = Sample();
        var pc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var tree = await SettledAsync(cloud, () => pc, () => pc += TimeSpan.FromMinutes(5), withRoot: true);
        pc += TimeSpan.FromMinutes(5);
        cloud.ResetCount();

        var again = await CloudWalker.WalkAsync(cloud.ListAsync, tree, progress: null, "test", CancellationToken.None, () => pc, cloud.RootTicks);

        // Its own time comes from its parent's listing - the one request the caller makes. Unchanged, nothing in it is read.
        Assert.Equal(0, cloud.Listings);
        var full = await CloudWalker.WalkAsync(cloud.ListAsync, previous: null, progress: null, "test", CancellationToken.None, () => pc, cloud.RootTicks);
        Assert.Equal(Sorted(full.Entries), Sorted(again.Entries));
    }

    [Fact]
    public async Task An_unchanged_whole_account_is_read_at_the_top_and_its_folders()
    {
        // A whole account has no parent that tells its time: its top and the folders in it are read.
        var cloud = Sample();
        var pc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var tree = await SettledAsync(cloud, () => pc, () => pc += TimeSpan.FromMinutes(5), withRoot: false);
        pc += TimeSpan.FromMinutes(5);
        cloud.ResetCount();

        await CloudWalker.WalkAsync(cloud.ListAsync, tree, progress: null, "test", CancellationToken.None, () => pc);

        // The top and "Ordner 0" to "Ordner 2" - out of 34 folders.
        Assert.Equal(4, cloud.Listings);
    }

    [Fact]
    public async Task A_change_deep_down_reads_only_its_way_and_folders_whose_time_others_share()
    {
        var cloud = Sample();
        var pc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var tree = await SettledAsync(cloud, () => pc, () => pc += TimeSpan.FromMinutes(5), withRoot: true);
        pc += TimeSpan.FromMinutes(5);
        cloud.Advance(TimeSpan.FromMinutes(10));
        cloud.Write("Ordner 0/Unterordner 4/neu.txt", 7);
        cloud.ResetCount();

        var again = await CloudWalker.WalkAsync(cloud.ListAsync, tree, progress: null, "test", CancellationToken.None, () => pc, cloud.RootTicks);

        // The way: the top, "Ordner 0", "Unterordner 4". Read to make sure, because a folder shares its time with the
        // sub-folder of its newest change: "Ordner 1" and "Ordner 2" (beside the way), "Unterordner 9" (the old newest
        // in "Ordner 0"). Out of 34 folders.
        Assert.Equal(6, cloud.Listings);
        Assert.Contains(again.Entries, e => e.Path == "Ordner 0/Unterordner 4/neu.txt");
    }
}
