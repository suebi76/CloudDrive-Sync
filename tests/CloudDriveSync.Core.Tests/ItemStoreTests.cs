using CloudDriveSync.Core.OnDemand;

namespace CloudDriveSync.Core.Tests;

public class ItemStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cd-items-" + Guid.NewGuid().ToString("N")[..8]);

    private string File => Path.Combine(_folder, "items.db");

    public void Dispose()
    {
        // SQLite keeps no file open once the store is disposed.
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Keeps_items_between_openings()
    {
        long id;
        using (var store = ItemStore.Open(File))
        {
            id = store.Add(new SyncItem(0, "Ordner/Übung 1.txt", false, 31, 638_000_000_000_000_000, null, 31, 638_000_000_000_000_001));
            store.Add(new SyncItem(0, "Ordner", true, 0, 638_000_000_000_000_000, null, null, null));
            store.SetMeta("first-run", "done");
        }
        using (var store = ItemStore.Open(File))
        {
            var item = store.Find(id)!;
            Assert.Equal("Ordner/Übung 1.txt", item.Path);
            Assert.Equal(31, item.CloudSize);
            Assert.Equal(638_000_000_000_000_001, item.LocalTicks);
            Assert.Equal(id, store.FindByPath("Ordner/Übung 1.txt")!.Id);
            Assert.Equal(2, store.All().Count);
            Assert.Equal("done", store.GetMeta("first-run"));
        }
    }

    [Fact]
    public void Updates_and_removes_items()
    {
        using var store = ItemStore.Open(File);
        var id = store.Add(new SyncItem(0, "a.txt", false, 1, 1, "sha1:aa", null, null));
        store.Update(new SyncItem(id, "b.txt", false, 2, 2, null, 2, 2));
        Assert.Null(store.FindByPath("a.txt"));
        Assert.Equal(2, store.Find(id)!.CloudSize);
        Assert.Null(store.Find(id)!.CloudHash);
        store.Remove(id);
        Assert.Null(store.Find(id));
    }

    [Fact]
    public void A_batch_is_kept_completely_or_not_at_all()
    {
        using var store = ItemStore.Open(File);
        store.Batch(() =>
        {
            store.Add(new SyncItem(0, "eins.txt", false, 1, 1, null, null, null));
            store.Add(new SyncItem(0, "zwei.txt", false, 1, 1, null, null, null));
        });
        Assert.ThrowsAny<Exception>(() => store.Batch(() =>
        {
            store.Add(new SyncItem(0, "drei.txt", false, 1, 1, null, null, null));
            // The same path twice breaks the batch: "drei.txt" must not stay either.
            store.Add(new SyncItem(0, "eins.txt", false, 1, 1, null, null, null));
        }));
        Assert.Equal(["eins.txt", "zwei.txt"], store.All().Select(i => i.Path));
    }

    [Fact]
    public void The_identity_carries_ID_and_path()
    {
        var identity = ItemIdentity.Encode(42, "Ordner/Was?.docx");
        Assert.Equal((42L, "Ordner/Was?.docx"), ItemIdentity.Decode(identity));
        Assert.Null(ItemIdentity.Decode("fremd"u8));
        // Too long for Windows: only the ID stays.
        var tooLong = ItemIdentity.Encode(7, new string('x', 5000));
        Assert.True(tooLong.Length <= 4096);
        Assert.Equal((7L, ""), ItemIdentity.Decode(tooLong));
    }
}
