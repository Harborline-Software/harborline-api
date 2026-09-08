using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Drafts;
using Harborline.Api.LocalNodeHost.Data.Leases;
using Harborline.Api.LocalNodeHost.Data.Properties;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.NodeLocalStore;

/// <summary>
/// ADR 0115 D8 Stage 2 Cohort C — node-local properties / leases store
/// round-trips on the REAL keyed (SQLCipher-encrypted) SQLite store, exercised
/// through the same SC-1 registration path the host uses
/// (<see cref="LocalNodeSqlCipherRegistration.AddSqlCipherLocalNodeDbContext"/>).
/// Mirrors <c>MaintenanceStoreTests</c>: proves (1) each schema is created in the
/// encrypted store, (2) writes persist + read back, (3) rows are encrypted at
/// rest (SC-1), and (4) each context uses a DISTINCT migration-history table so
/// the contexts' MigrateAsync calls don't clobber each other. (Payments is
/// deferred — the kernel-ledger already owns a `payments` table in this store.)
/// </summary>
public sealed class NodeLocalDoctypeStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public NodeLocalDoctypeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-nodelocal-pml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");
    }

    private static byte[] FreshRootSeed()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        return seed;
    }

    private static IReadOnlyList<IHarborlineEntityModule> NoModules() => [];

    /// <summary>
    /// Builds a host with the SC-1 registration and returns the started host. The
    /// two reviewed startup owners have migrated every catalog-owned context by the time
    /// <c>StartAsync</c> returns.
    /// </summary>
    private async Task<IHost> StartKeyedHostAsync(byte[] rootSeed)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEnumerable<IHarborlineEntityModule>>(_ => NoModules());
        builder.Services.AddSqlCipherLocalNodeDbContext(
            rootSeed: rootSeed,
            databasePath: _dbPath,
            keyDerivation: new SqlCipherKeyDerivation());
        builder.Services.AddHostedService<NodeDraftsMigrator>();
        builder.Services.ValidateLocalNodeExclusiveEfContexts();

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private async Task<IHost> StartKeyedHostWithStoreDekAsync(byte[] storeDek)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEnumerable<IHarborlineEntityModule>>(_ => NoModules());
        builder.Services.AddSqlCipherLocalNodeDbContextWithStoreDek(storeDek, _dbPath);
        builder.Services.AddHostedService<NodeDraftsMigrator>();
        builder.Services.ValidateLocalNodeExclusiveEfContexts();

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    [Fact(DisplayName = "Node-local store: property create → read round-trips on the keyed store")]
    public async Task Property_Create_Read_RoundTrips()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var factory = host.Services.GetRequiredService<IDbContextFactory<NodeLocalPropertyDbContext>>();
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.Properties.Add(new PropertyRecord
            {
                Name = "PROP-0001",
                PropertyName = "150 Lexington Ct",
                City = "Springfield",
                Units = 4,
                Status = "Active",
                Company = "Acme",
                CreatedAt = now,
                ModifiedAt = now,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var row = await ctx.Properties.AsNoTracking().SingleAsync(p => p.Name == "PROP-0001");
            Assert.Equal("150 Lexington Ct", row.PropertyName);
            Assert.Equal(4, row.Units);
            Assert.Equal("Active", row.Status);
        }

        await host.StopAsync();
    }

    [Fact(DisplayName = "Node-local store: lease create → read round-trips (decimal + bool persist)")]
    public async Task Lease_Create_Read_RoundTrips()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var factory = host.Services.GetRequiredService<IDbContextFactory<NodeLocalLeaseDbContext>>();
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.Leases.Add(new LeaseRecord
            {
                Name = "LEASE-0001",
                Tenant = "Jane Tenant",
                Property = "PROP-0001",
                Unit = "4B",
                StartDate = "2026-01-01",
                EndDate = "2026-12-31",
                MonthlyRent = 1850.50m,
                Status = "Active",
                TermCadence = "monthly",
                AutoRenew = true,
                CreatedAt = now,
                ModifiedAt = now,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var row = await ctx.Leases.AsNoTracking().SingleAsync(l => l.Name == "LEASE-0001");
            Assert.Equal("Jane Tenant", row.Tenant);
            Assert.Equal(1850.50m, row.MonthlyRent);
            Assert.True(row.AutoRenew);
            Assert.Equal("monthly", row.TermCadence);
        }

        await host.StopAsync();
    }

    [Fact(DisplayName = "Node-local store: property/lease rows are encrypted at rest (raw unkeyed open fails)")]
    public async Task NodeLocalRows_AreEncryptedAtRest()
    {
        using (var host = await StartKeyedHostAsync(FreshRootSeed()))
        {
            var now = DateTimeOffset.UtcNow;
            var propFactory = host.Services.GetRequiredService<IDbContextFactory<NodeLocalPropertyDbContext>>();
            await using (var ctx = await propFactory.CreateDbContextAsync())
            {
                ctx.Properties.Add(new PropertyRecord
                {
                    Name = "PROP-SECRET",
                    PropertyName = "Sensitive address",
                    CreatedAt = now,
                    ModifiedAt = now,
                });
                await ctx.SaveChangesAsync();
            }
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        // (a) The file does not begin with the plaintext SQLite magic header.
        var bytes = await File.ReadAllBytesAsync(_dbPath);
        var magic = "SQLite format 3\0"u8.ToArray();
        var headerMatches = bytes.Length >= magic.Length
            && bytes.AsSpan(0, magic.Length).SequenceEqual(magic);
        Assert.False(headerMatches, "node-local store begins with plaintext SQLite header (SC-1 violation)");

        // (b) A raw UNKEYED open cannot read the node-local tables.
        using var raw = new SqliteConnection($"Data Source={_dbPath};");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM properties;";
        Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
    }

    [Fact(DisplayName = "Node-local store: all catalog migrations are exact and restart-idempotent")]
    public async Task EveryCatalogContext_MigratesExactly_And_RestartIsIdempotent()
    {
        var seed = FreshRootSeed();
        using (var host = await StartKeyedHostAsync(seed))
        {
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        // A fresh provider over the same encrypted file must replay as a no-op.
        using (var host = await StartKeyedHostAsync(seed))
        {
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        var dek = new SqlCipherKeyDerivation().DeriveSqlCipherKey(
            seed, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);
        AssertCatalogPhysicalEvidence(dek);
    }

    [Fact(DisplayName = "Node-local store: injected-DEK path applies the same exact catalog twice")]
    public async Task InjectedDek_EveryCatalogContext_MigratesExactly_And_RestartIsIdempotent()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        using (var host = await StartKeyedHostWithStoreDekAsync(dek))
        {
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        using (var host = await StartKeyedHostWithStoreDekAsync(dek))
        {
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        AssertCatalogPhysicalEvidence(dek);
    }

    private void AssertCatalogPhysicalEvidence(byte[] dek)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};");
        conn.Open();
        using (var keyCmd = conn.CreateCommand())
        {
            keyCmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(dek)}'\";";
            keyCmd.ExecuteNonQuery();
        }
        using var q = conn.CreateCommand();
        q.CommandText =
            "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE '%MigrationsHistory%';";
        var historyTables = new List<string>();
        using (var reader = q.ExecuteReader())
        {
            while (reader.Read())
            {
                historyTables.Add(reader.GetString(0));
            }
        }

        var expectedTables = LocalNodeExclusiveEfContextCatalog.All
            .Select(item => item.MigrationsHistoryTable)
            .Append("__EFMigrationsHistory")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedTables, historyTables.Order(StringComparer.Ordinal));

        foreach (var descriptor in LocalNodeExclusiveEfContextCatalog.All)
        {
            using var history = conn.CreateCommand();
            history.CommandText =
                $"SELECT MigrationId FROM \"{descriptor.MigrationsHistoryTable}\" ORDER BY MigrationId;";
            var observedIds = new List<string>();
            using var reader = history.ExecuteReader();
            while (reader.Read())
            {
                observedIds.Add(reader.GetString(0));
            }

            Assert.Equal(
                descriptor.CompiledMigrations.Select(item => item.MigrationId),
                observedIds);
        }

        using var searchObjects = conn.CreateCommand();
        searchObjects.CommandText =
            "SELECT name FROM sqlite_schema " +
            "WHERE name IN ('search_fts', 'search_nodes_ai', 'search_nodes_ad', 'search_nodes_au') " +
            "ORDER BY name;";
        var observedSearchObjects = new List<string>();
        using (var reader = searchObjects.ExecuteReader())
        {
            while (reader.Read())
            {
                observedSearchObjects.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            new[] { "search_fts", "search_nodes_ad", "search_nodes_ai", "search_nodes_au" },
            observedSearchObjects);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
