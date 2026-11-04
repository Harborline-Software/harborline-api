using System.Text.Json;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>Single-writer, encrypted SQLite backing for the producer's unit of work.</summary>
/// <remarks>
/// <para>Each producer <c>Save(ContentStoreChange)</c> runs under <see cref="SyncRoot"/> and writes exactly the named
/// entries in one SQLite transaction (<c>synchronous=EXTRA</c>, rollback journal, <c>fullfsync</c> on macOS), so SQLite
/// performs the journal synchronisation and atomic commit (https://sqlite.org/atomiccommit.html). Disposal of an
/// uncommitted transaction rolls back and the producer restores memory (CWE-460). There is no shutdown save.</para>
/// <para>The database is keyed in SQLCipher's raw-key form from bytes HKDF-derived by
/// <see cref="NodeContentStoreComposition"/>, and refuses to open unless the native library reports a
/// <c>cipher_version</c>. A lifetime OS file lease keeps one writer per store directory, so no second process caches
/// divergent quota or owner state (CWE-362, CWE-367); network filesystems are not supported. Tenant and content ids
/// are SQL parameters, never path components (CWE-22). No item-limit, quota-fraction or expiry default is supplied:
/// DES-0054 assigns those to DES-0037.</para>
/// </remarks>
public sealed class NodeContentStoreStorage : IContentStoreStorage, IDisposable
{
    private readonly SqliteConnection _database;
    private readonly FileStream _lease;
    public string DirectoryPath { get; }
    public object SyncRoot { get; } = new();
    public IDictionary<ContentStorageItemKey, ContentStoreItem> Items { get; } = new Dictionary<ContentStorageItemKey, ContentStoreItem>();
    public IDictionary<Guid, ContentStoreItem> StagedItems { get; } = new Dictionary<Guid, ContentStoreItem>();
    public IList<ReclamationEntry> Reclamations { get; } = new List<ReclamationEntry>();
    public IDictionary<(TenantId TenantId, Guid ItemKey), ContentIntegrityEntry> IntegrityEntries { get; } = new Dictionary<(TenantId, Guid), ContentIntegrityEntry>();

    public NodeContentStoreStorage(string directory, ReadOnlySpan<byte> databaseKey)
    {
        if (databaseKey.Length != 32) throw new ArgumentException("A 256-bit store key is required.", nameof(databaseKey));
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        // Only installation-owned paths; tenant/content ids never become paths (CWE-22).
        // Hold an OS lease for the lifetime of the cached state (CWE-362/CWE-367).
        _lease = new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "content.db"), Pooling = false,
        }.ToString());
        try
        {
            _database.Open();
            // Raw-key hex form, as SqlCipherConnectionInterceptor does: the derived key is already full entropy.
            Execute($"PRAGMA key = \"x'{Convert.ToHexString(databaseKey)}'\";");
            // Fail closed if the loaded native library is not SQLCipher; PRAGMA key is otherwise a silent no-op (SC-1).
            using (var cipher = _database.CreateCommand())
            {
                cipher.CommandText = "PRAGMA cipher_version;";
                if (cipher.ExecuteScalar() is not string { Length: > 0 })
                    throw new InvalidOperationException("The content store requires the SQLCipher native library.");
            }
            Execute("PRAGMA journal_mode=DELETE; PRAGMA synchronous=EXTRA; PRAGMA fullfsync=ON; PRAGMA secure_delete=ON;");
            Execute("CREATE TABLE IF NOT EXISTS entries (kind TEXT NOT NULL, key TEXT NOT NULL, value BLOB NOT NULL, PRIMARY KEY(kind,key));");
            using var command = _database.CreateCommand();
            command.CommandText = "SELECT kind,key,value FROM entries ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bytes = (byte[])reader[2];
                switch (reader.GetString(0))
                {
                    case "item":
                        var item = JsonSerializer.Deserialize<StoredItem>(bytes)!.Restore();
                        Items.Add(new(item.Tenant, item.ContentId), item);
                        break;
                    case "stage":
                        var stage = JsonSerializer.Deserialize<StoredItem>(bytes)!.Restore();
                        StagedItems.Add(stage.StageId, stage);
                        break;
                    case "reclamation": Reclamations.Add(JsonSerializer.Deserialize<StoredReclamation>(bytes)!.Restore()); break;
                    case "integrity":
                        var entry = JsonSerializer.Deserialize<ContentIntegrityEntry>(bytes)!;
                        IntegrityEntries.Add((entry.TenantId, entry.ItemKey), entry);
                        break;
                    default: throw new InvalidDataException("Unknown content-store entry kind.");
                }
            }
        }
        catch
        {
            _database.Dispose();
            _lease.Dispose();
            throw;
        }
    }

    public void Save(ContentStoreChange change)
    {
        if (!Monitor.IsEntered(SyncRoot)) throw new InvalidOperationException("Save requires the storage lock.");
        using var transaction = _database.BeginTransaction();
        // Disposal rolls back every write on failure (CWE-460). Never acknowledge before COMMIT/fsync.
        foreach (var key in change.Items)
            Write("item", JsonSerializer.Serialize(new[] { key.TenantId.Value, key.ContentId.Value }),
                Items.TryGetValue(key, out var item) ? JsonSerializer.SerializeToUtf8Bytes(StoredItem.From(item)) : null, transaction);
        foreach (var key in change.StagedItems)
            Write("stage", key.ToString("D"), StagedItems.TryGetValue(key, out var stage) ? JsonSerializer.SerializeToUtf8Bytes(StoredItem.From(stage)) : null, transaction);
        foreach (var entry in change.Reclamations)
            Write("reclamation", Guid.NewGuid().ToString("D"), JsonSerializer.SerializeToUtf8Bytes(new StoredReclamation(entry.ContentId.Value, entry.TenantId.Value, entry.Cause, entry.Run)), transaction);
        foreach (var key in change.IntegrityEntries)
            Write("integrity", JsonSerializer.Serialize(new[] { key.TenantId.Value, key.ItemKey.ToString("D") }),
                IntegrityEntries.TryGetValue(key, out var integrity) ? JsonSerializer.SerializeToUtf8Bytes(integrity) : null, transaction);
        transaction.Commit();
    }

    private void Write(string kind, string key, byte[]? value, SqliteTransaction transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = value is null ? "DELETE FROM entries WHERE kind=$kind AND key=$key" :
            "INSERT INTO entries(kind,key,value) VALUES($kind,$key,$value) ON CONFLICT(kind,key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", key);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = _database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (SyncRoot) { _database.Dispose(); _lease.Dispose(); }
    }

    private sealed record StoredReclamation(string Id, string Tenant, string Cause, string Run)
    {
        public ReclamationEntry Restore() => new(new(Id), new(Tenant), Cause, Run);
    }

    private sealed record StoredItem(Guid StageId, string Tenant, string Id, long Length, string MediaType, DateTimeOffset CreatedUtc,
        byte[] WrappedDataKey, byte[] WrapNonce, byte[] WrapTag, byte[] NoncePrefix, int SegmentSize,
        List<ContentCiphertextSegment> Segments, ContentOwner[] Owners, ContentLifecycleState State)
    {
        public static StoredItem From(ContentStoreItem item) => new(item.StageId, item.Tenant.Value, item.ContentId.Value, item.Length,
            item.MediaType, item.CreatedUtc, item.WrappedDataKey, item.WrapNonce, item.WrapTag, item.NoncePrefix, item.SegmentSize,
            item.Segments, [.. item.Owners], item.State);
        public ContentStoreItem Restore()
        {
            var item = new ContentStoreItem(StageId, new(Tenant), new(Id), Length, MediaType, CreatedUtc, WrappedDataKey, WrapNonce, WrapTag, NoncePrefix, SegmentSize, Segments) { State = State };
            item.Owners.AddRange(Owners);
            return item;
        }
    }
}
