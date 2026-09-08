using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Packs;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Test harness that stands up a REAL SQLCipher-encrypted <see cref="NodeLocalPacksDbContext"/> with the durable
/// pack-install migration applied (pack_installed_versions / pack_watermarks / pack_overrides /
/// pack_key_ownership), keyed under a raw hex DEK. Mirrors <c>SearchTestStore</c> — the proven raw-key-hex
/// <c>PRAGMA key</c> + <c>Pooling=False</c> pattern — so the F5 store is exercised against the encrypted file, not
/// plaintext, and <see cref="Reopen"/> faithfully models a process restart against the SAME on-disk state.
/// </summary>
public sealed class PacksTestStore : IAsyncDisposable
{
    private static int s_cipherProviderBound;
    private readonly string _dir;
    private readonly byte[] _key;
    private readonly bool _ownsDirectory;

    private PacksTestStore(string dir, byte[] key, string dbPath, bool ownsDirectory = true)
    {
        _dir = dir;
        _key = key;
        DatabasePath = dbPath;
        _ownsDirectory = ownsDirectory;
    }

    /// <summary>Absolute path to this harness's encrypted SQLite file.</summary>
    public string DatabasePath { get; }

    /// <summary>Create a keyed encrypted store and apply the packs migration to it.</summary>
    public static async Task<PacksTestStore> CreateAsync(byte keySalt = 11)
    {
        EnsureCipherProvider();

        var dir = Path.Combine(Path.GetTempPath(), "harborline-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "local-node.db");

        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + keySalt);
        }

        var store = new PacksTestStore(dir, key, dbPath);

        await using var ctx = store.CreateContext();
        await ctx.Database.MigrateAsync();
        return store;
    }

    /// <summary>
    /// Reopen the SAME on-disk encrypted file under the SAME key as <paramref name="origin"/> WITHOUT re-migrating
    /// (the schema already exists) — models a process restart against durable state. The reopened handle does NOT
    /// own the temp directory, so disposing it leaves the file for <paramref name="origin"/>'s disposal.
    /// </summary>
    public static PacksTestStore Reopen(PacksTestStore origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        EnsureCipherProvider();
        return new PacksTestStore(origin._dir, origin._key, origin.DatabasePath, ownsDirectory: false);
    }

    /// <summary>
    /// Build a fresh keyed <see cref="NodeLocalPacksDbContext"/> over this harness's encrypted file. A live
    /// <see cref="SqliteConnection"/> is opened and keyed via <c>PRAGMA key</c> BEFORE EF touches it (the most
    /// robust SQLCipher+EF pattern), then handed to EF as an already-open connection the context owns + disposes.
    /// </summary>
    public NodeLocalPacksDbContext CreateContext()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using (var keyCmd = connection.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(_key)}'\";";
            keyCmd.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<NodeLocalPacksDbContext>()
            .UseSqlite(
                connection,
                contextOwnsConnection: true,
                sqliteOptionsAction: sqlite => sqlite.MigrationsHistoryTable(
                    NodeLocalPacksDbContext.MigrationsHistoryTableName))
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new NodeLocalPacksDbContext(options);
    }

    /// <summary>An <see cref="IDbContextFactory{TContext}"/> over this harness — the seam the durable store takes.</summary>
    public IDbContextFactory<NodeLocalPacksDbContext> Factory => new HarnessFactory(this);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsDirectory)
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
        return ValueTask.CompletedTask;
    }

    private static void EnsureCipherProvider()
    {
        // SC-1: pin the cipher provider explicitly, else a bare init can bind the non-cipher e_sqlite3 and
        // PRAGMA key becomes a silent no-op (plaintext). Same hygiene as SearchTestStore / DekPairingResolverArchFence.
        if (System.Threading.Interlocked.Exchange(ref s_cipherProviderBound, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
        }
    }

    private sealed class HarnessFactory : IDbContextFactory<NodeLocalPacksDbContext>
    {
        private readonly PacksTestStore _store;
        public HarnessFactory(PacksTestStore store) => _store = store;
        public NodeLocalPacksDbContext CreateDbContext() => _store.CreateContext();
    }
}
