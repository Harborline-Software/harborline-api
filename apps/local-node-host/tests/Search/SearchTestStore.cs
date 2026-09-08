using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

/// <summary>
/// Test harness that stands up a REAL SQLCipher-encrypted <see cref="NodeLocalSearchDbContext"/> with the
/// search migration applied (search_nodes / search_edges + the FTS5 trigram virtual table + sync triggers),
/// keyed under a per-tenant DEK. Mirrors the proven raw-key-hex <c>PRAGMA key</c> pattern the
/// <c>DekPairingResolverArchFence</c> uses — so the tests run against the encrypted store, not plaintext.
/// </summary>
/// <remarks>
/// A DISTINCT key per harness instance models the per-tenant encrypted FILE = the cross-tenant isolation
/// boundary: a context built under tenant-A's key cannot open tenant-B's file.
/// </remarks>
public sealed class SearchTestStore : IAsyncDisposable
{
    private static int s_cipherProviderBound;
    private readonly string _dir;
    private readonly byte[] _key;
    private readonly bool _ownsDirectory;

    private SearchTestStore(string dir, byte[] key, string dbPath, bool ownsDirectory = true)
    {
        _dir = dir;
        _key = key;
        DatabasePath = dbPath;
        _ownsDirectory = ownsDirectory;
    }

    /// <summary>Absolute path to this harness's encrypted SQLite file.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Create a keyed encrypted store and apply the search migration to it. <paramref name="keySalt"/>
    /// distinguishes per-tenant files (different salt ⇒ different DEK ⇒ different, mutually-unopenable file).
    /// </summary>
    public static async Task<SearchTestStore> CreateAsync(byte keySalt = 7)
        => await CreateAtMigrationAsync(targetMigration: null, keySalt).ConfigureAwait(false);

    /// <summary>
    /// Create an empty keyed database and migrate it directly to <paramref name="targetMigration"/>.
    /// Passing null applies the latest schema. A historical target never executes a later migration's Down.
    /// </summary>
    public static async Task<SearchTestStore> CreateAtMigrationAsync(
        string? targetMigration,
        byte keySalt = 7)
    {
        EnsureCipherProvider();

        var dir = Path.Combine(Path.GetTempPath(), "harborline-kgsearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "local-node.db");

        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + keySalt);
        }

        var store = new SearchTestStore(dir, key, dbPath);

        await using var ctx = store.CreateContext();
        await ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
            .MigrateAsync(targetMigration);
        return store;
    }

    /// <summary>
    /// Reopen the SAME on-disk encrypted file under the SAME key as <paramref name="origin"/> WITHOUT
    /// re-migrating (the schema already exists) — models a process restart against durable state. The reopened
    /// handle does NOT own the temp directory, so disposing it leaves the file for <paramref name="origin"/>'s
    /// disposal (the test owns both lifetimes).
    /// </summary>
    public static SearchTestStore Reopen(SearchTestStore origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        EnsureCipherProvider();
        return new SearchTestStore(origin._dir, origin._key, origin.DatabasePath, ownsDirectory: false);
    }

    /// <summary>
    /// Build a fresh keyed <see cref="NodeLocalSearchDbContext"/> over this harness's encrypted file. A live
    /// <see cref="SqliteConnection"/> is opened and keyed via <c>PRAGMA key</c> BEFORE EF touches it (the
    /// most robust SQLCipher+EF pattern — no interceptor-timing ambiguity), then handed to EF as an already-
    /// open connection. The context owns + disposes the connection.
    /// </summary>
    public NodeLocalSearchDbContext CreateContext()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using (var keyCmd = connection.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(_key)}'\";";
            keyCmd.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<NodeLocalSearchDbContext>()
            .UseSqlite(
                connection,
                contextOwnsConnection: true,
                sqliteOptionsAction: sqlite => sqlite.MigrationsHistoryTable(
                    NodeLocalSearchDbContext.MigrationsHistoryTableName))
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        // contextOwnsConnection: true ⇒ disposing the context closes + disposes this keyed connection.
        return new NodeLocalSearchDbContext(options);
    }

    /// <summary>
    /// Build the installation-identity context over this same encrypted local-node database, matching the
    /// production composition where independently migrated contexts share one physical store.
    /// </summary>
    public NodeLocalInstallationIdentityDbContext CreateInstallationIdentityContext()
    {
        var connection = OpenKeyedConnection();
        var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
            .UseSqlite(
                connection,
                contextOwnsConnection: true,
                sqliteOptionsAction: sqlite => sqlite.MigrationsHistoryTable(
                    NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new NodeLocalInstallationIdentityDbContext(options);
    }

    public Data.Roster.NodeLocalRosterDbContext CreateRosterContext() => new(
        new DbContextOptionsBuilder<Data.Roster.NodeLocalRosterDbContext>()
            .UseSqlite(OpenKeyedConnection(), contextOwnsConnection: true,
                sqliteOptionsAction: sqlite => sqlite.MigrationsHistoryTable(
                    Data.Roster.NodeLocalRosterDbContext.MigrationsHistoryTableName)).Options);

    /// <summary>An <see cref="IDbContextFactory{TContext}"/> over this harness for services that need one.</summary>
    public IDbContextFactory<NodeLocalSearchDbContext> Factory => new HarnessFactory(this);

    /// <summary>An installation-identity factory over the same encrypted file as <see cref="Factory"/>.</summary>
    public IDbContextFactory<NodeLocalInstallationIdentityDbContext> InstallationIdentityFactory =>
        new InstallationIdentityHarnessFactory(this);

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
        // PRAGMA key becomes a silent no-op (plaintext). Same hygiene as DekPairingResolverArchFence.
        if (System.Threading.Interlocked.Exchange(ref s_cipherProviderBound, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
        }
    }

    private SqliteConnection OpenKeyedConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var keyCmd = connection.CreateCommand();
        keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(_key)}'\";";
        keyCmd.ExecuteNonQuery();
        return connection;
    }

    private sealed class HarnessFactory : IDbContextFactory<NodeLocalSearchDbContext>
    {
        private readonly SearchTestStore _store;
        public HarnessFactory(SearchTestStore store) => _store = store;
        public NodeLocalSearchDbContext CreateDbContext() => _store.CreateContext();
    }

    private sealed class InstallationIdentityHarnessFactory(SearchTestStore store)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext() =>
            store.CreateInstallationIdentityContext();
    }
}
