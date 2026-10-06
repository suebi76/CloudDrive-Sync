using Microsoft.Data.Sqlite;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// One file or folder of a synchronisation with files on demand, as it was when both sides were last in step: the
/// version in the cloud (size, the server's time, a checksum where the server has one) and the version on the PC
/// (size and time of the file, when its data was on the PC). The ID never changes - it travels in the placeholder, so
/// a file renamed on the PC is still recognised.
/// </summary>
/// <param name="Path">Path in the cloud folder, "/" separated, in rclone's standard encoding.</param>
/// <param name="CloudTicks">The server's modification time (UTC ticks), exactly as the listing reported it.</param>
/// <param name="LocalTicks">The file's last write time on the PC (UTC ticks); null when its data was not on the PC.</param>
public sealed record SyncItem(long Id, string Path, bool IsDirectory, long CloudSize, long CloudTicks, string? CloudHash, long? LocalSize, long? LocalTicks);

/// <summary>
/// The state of a synchronisation with files on demand, in an SQLite database (sync\&lt;id&gt;\items.db). Every change is
/// a transaction written through to the disk (WAL, synchronous=FULL), so a crash or a power cut never leaves half a
/// state behind. SQLite comes from Windows itself (winsqlite3). One connection, used under a lock: requests for data
/// (other threads) read while a run writes.
/// </summary>
public sealed class ItemStore : IDisposable
{
    private const int Schema = 1;
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();
    // While a batch runs, every command belongs to its transaction (Microsoft.Data.Sqlite insists on it).
    private SqliteTransaction? _transaction;

    static ItemStore() => SQLitePCL.Batteries_V2.Init();

    private ItemStore(SqliteConnection connection) => _connection = connection;

    public static ItemStore Open(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        connection.Open();
        var store = new ItemStore(connection);
        try
        {
            store.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;");
            store.Execute("""
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS items (
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE,
                    is_directory INTEGER NOT NULL,
                    cloud_size INTEGER NOT NULL,
                    cloud_ticks INTEGER NOT NULL,
                    cloud_hash TEXT,
                    local_size INTEGER,
                    local_ticks INTEGER);
                CREATE TABLE IF NOT EXISTS on_disk (id INTEGER PRIMARY KEY, since INTEGER NOT NULL);
                """);
            var schema = store.GetMeta("schema");
            if (schema is null) store.SetMeta("schema", Schema.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (schema != Schema.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException($"{file}: unknown schema {schema}");
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public IReadOnlyList<SyncItem> All()
    {
        lock (_lock) return Query("SELECT * FROM items ORDER BY path", null);
    }

    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                using var command = NewCommand();
                command.CommandText = "SELECT EXISTS (SELECT 1 FROM items)";
                return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0;
            }
        }
    }

    public SyncItem? Find(long id)
    {
        lock (_lock) return Query("SELECT * FROM items WHERE id = $id", c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();
    }

    public SyncItem? FindByPath(string path)
    {
        lock (_lock) return Query("SELECT * FROM items WHERE path = $path", c => c.Parameters.AddWithValue("$path", path)).FirstOrDefault();
    }

    /// <summary>Adds an item and returns its new ID (the ID of <paramref name="item"/> is ignored).</summary>
    public long Add(SyncItem item)
    {
        lock (_lock)
        {
            using var command = NewCommand();
            command.CommandText = """
                INSERT INTO items (path, is_directory, cloud_size, cloud_ticks, cloud_hash, local_size, local_ticks)
                VALUES ($path, $dir, $size, $ticks, $hash, $localSize, $localTicks) RETURNING id
                """;
            Bind(command, item);
            return (long)command.ExecuteScalar()!;
        }
    }

    public void Update(SyncItem item)
    {
        lock (_lock)
        {
            using var command = NewCommand();
            command.CommandText = """
                UPDATE items SET path = $path, is_directory = $dir, cloud_size = $size, cloud_ticks = $ticks, cloud_hash = $hash,
                    local_size = $localSize, local_ticks = $localTicks WHERE id = $id
                """;
            Bind(command, item);
            command.Parameters.AddWithValue("$id", item.Id);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException($"item {item.Id} is not in the store");
        }
    }

    /// <summary>Adds the item, or updates the one at its path (keeping that one's ID); returns the ID.</summary>
    public long Upsert(SyncItem item)
    {
        lock (_lock)
        {
            if (FindByPath(item.Path) is { } existing)
            {
                Update(item with { Id = existing.Id });
                return existing.Id;
            }
            return Add(item);
        }
    }

    /// <summary>Gives an item - for a folder: with everything below it - a new place (renamed or moved).</summary>
    public void Move(string from, string to)
    {
        lock (_lock)
        {
            using var command = NewCommand();
            // Exactly the path, or the path followed by "/": "Alt" must not catch "Alter".
            command.CommandText = """
                UPDATE items SET path = $to || substr(path, length($from) + 1)
                WHERE path = $from OR substr(path, 1, length($from) + 1) = $from || '/'
                """;
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            command.ExecuteNonQuery();
        }
    }

    public void Remove(long id)
    {
        lock (_lock)
        {
            using var command = NewCommand();
            command.CommandText = "DELETE FROM items WHERE id = $id; DELETE FROM on_disk WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Since when the data of each file has been on the PC, as far as CloudDrive-Sync saw it (UTC ticks by item ID) - for
    /// freeing space automatically (<see cref="Executor.FreeUpSpace"/>).
    /// </summary>
    public Dictionary<long, long> OnDiskSince()
    {
        lock (_lock)
        {
            using var command = NewCommand();
            command.CommandText = "SELECT id, since FROM on_disk";
            using var reader = command.ExecuteReader();
            var result = new Dictionary<long, long>();
            while (reader.Read()) result[reader.GetInt64(0)] = reader.GetInt64(1);
            return result;
        }
    }

    /// <summary>Notes files whose data arrived on the PC and forgets those whose data left it, in one transaction.</summary>
    public void UpdateOnDisk(IReadOnlyDictionary<long, long> arrived, IReadOnlyCollection<long> left)
    {
        if (arrived.Count == 0 && left.Count == 0) return;
        Batch(() =>
        {
            foreach (var (id, since) in arrived)
            {
                using var command = NewCommand();
                command.CommandText = "INSERT INTO on_disk (id, since) VALUES ($id, $since) ON CONFLICT(id) DO UPDATE SET since = excluded.since";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$since", since);
                command.ExecuteNonQuery();
            }
            foreach (var id in left)
            {
                using var command = NewCommand();
                command.CommandText = "DELETE FROM on_disk WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Runs several changes as one transaction: all of them are kept or none. Requests for data wait meanwhile, so
    /// batches stay short (one folder, one step of a run).
    /// </summary>
    public void Batch(Action changes)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            _transaction = transaction;
            try
            {
                changes();
                transaction.Commit();
            }
            finally
            {
                _transaction = null;
            }
        }
    }

    public string? GetMeta(string key)
    {
        lock (_lock)
        {
            using var command = NewCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            using var command = NewCommand();
            command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    private List<SyncItem> Query(string sql, Action<SqliteCommand>? bind)
    {
        using var command = NewCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        using var reader = command.ExecuteReader();
        var items = new List<SyncItem>();
        while (reader.Read())
        {
            items.Add(new SyncItem(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3), reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }
        return items;
    }

    private static void Bind(SqliteCommand command, SyncItem item)
    {
        command.Parameters.AddWithValue("$path", item.Path);
        command.Parameters.AddWithValue("$dir", item.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$size", item.CloudSize);
        command.Parameters.AddWithValue("$ticks", item.CloudTicks);
        command.Parameters.AddWithValue("$hash", (object?)item.CloudHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$localSize", (object?)item.LocalSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$localTicks", (object?)item.LocalTicks ?? DBNull.Value);
    }

    private SqliteCommand NewCommand()
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = NewCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_lock) _connection.Dispose();
    }
}
