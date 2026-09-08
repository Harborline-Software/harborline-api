using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Maintenance;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Maintenance;

/// <summary>
/// ADR 0115 D8 Stage 2 — node-local maintenance store round-trip on the REAL
/// keyed (SQLCipher-encrypted) SQLite store, exercised through the same SC-1
/// registration path the host uses
/// (<see cref="LocalNodeSqlCipherRegistration.AddSqlCipherLocalNodeDbContext"/>).
/// </summary>
/// <remarks>
/// <para>
/// These are on-disk tests (not <c>:memory:</c>): the registration installs the
/// SqlCipher interceptor + the startup encryption guard, which migrates BOTH the
/// financial <see cref="LocalNodeDbContext"/> and the node-local
/// <see cref="NodeLocalMaintenanceDbContext"/> against the same keyed file. The
/// round-trip proves: (1) the maintenance schema is created in the encrypted
/// store, (2) writes persist + read back, and (3) the maintenance rows are
/// encrypted at rest (SC-1) — a raw unkeyed open fails.
/// </para>
/// </remarks>
public sealed class MaintenanceStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public MaintenanceStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-maint-" + Guid.NewGuid().ToString("N"));
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
    /// Builds a host with the SC-1 registration and returns the started host so
    /// the test can resolve the maintenance context factory and operate on the
    /// keyed store. The encryption guard has already migrated both contexts by
    /// the time <c>StartAsync</c> returns.
    /// </summary>
    private async Task<IHost> StartKeyedHostAsync(byte[] rootSeed)
    {
        var builder = Host.CreateApplicationBuilder();
        // No financial entity modules — keeps the financial schema minimal; this
        // test is about the maintenance context, which is independent of the
        // module set.
        builder.Services.AddSingleton<IEnumerable<IHarborlineEntityModule>>(_ => NoModules());
        builder.Services.AddSqlCipherLocalNodeDbContext(
            rootSeed: rootSeed,
            databasePath: _dbPath,
            keyDerivation: new SqlCipherKeyDerivation());

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    [Fact(DisplayName = "Maintenance store: create → read → update round-trips on the keyed store")]
    public async Task Create_Read_Update_RoundTrips()
    {
        using var host = await StartKeyedHostAsync(FreshRootSeed());
        var factory = host.Services
            .GetRequiredService<IDbContextFactory<NodeLocalMaintenanceDbContext>>();

        var now = DateTimeOffset.UtcNow;

        // Create.
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.MaintenanceTickets.Add(new MaintenanceTicketRecord
            {
                Name = "TKT-00001",
                Subject = "Leaky faucet",
                Property = "PROP-1",
                Status = "Open",
                Priority = "High",
                CreatedAt = now,
                ModifiedAt = now,
            });
            await ctx.SaveChangesAsync();
        }

        // Read back (fresh context — proves it persisted to the file, not the
        // change-tracker).
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var row = await ctx.MaintenanceTickets.AsNoTracking()
                .SingleAsync(t => t.Name == "TKT-00001");
            Assert.Equal("Leaky faucet", row.Subject);
            Assert.Equal("PROP-1", row.Property);
            Assert.Equal("Open", row.Status);
            Assert.Equal("High", row.Priority);
        }

        // Update.
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var row = await ctx.MaintenanceTickets.SingleAsync(t => t.Name == "TKT-00001");
            row.Status = "In Progress";
            row.AssignedTo = "tech-jane";
            row.Cost = 125.50m;
            row.ModifiedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }

        // Verify the update persisted.
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var row = await ctx.MaintenanceTickets.AsNoTracking()
                .SingleAsync(t => t.Name == "TKT-00001");
            Assert.Equal("In Progress", row.Status);
            Assert.Equal("tech-jane", row.AssignedTo);
            Assert.Equal(125.50m, row.Cost);
        }

        await host.StopAsync();
    }

    [Fact(DisplayName = "Maintenance store: rows are encrypted at rest (raw unkeyed open fails)")]
    public async Task MaintenanceRows_AreEncryptedAtRest()
    {
        using (var host = await StartKeyedHostAsync(FreshRootSeed()))
        {
            var factory = host.Services
                .GetRequiredService<IDbContextFactory<NodeLocalMaintenanceDbContext>>();
            await using (var ctx = await factory.CreateDbContextAsync())
            {
                ctx.MaintenanceTickets.Add(new MaintenanceTicketRecord
                {
                    Name = "TKT-SECRET",
                    Subject = "Sensitive tenant complaint",
                    Property = "PROP-9",
                    CreatedAt = DateTimeOffset.UtcNow,
                    ModifiedAt = DateTimeOffset.UtcNow,
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
        Assert.False(headerMatches, "maintenance store begins with plaintext SQLite header (SC-1 violation)");

        // (b) A raw UNKEYED open cannot read the maintenance table.
        using var raw = new SqliteConnection($"Data Source={_dbPath};");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM maintenance_tickets;";
        Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
    }

    [Fact(DisplayName = "Maintenance store: uses a distinct migration-history table from the financial store")]
    public async Task UsesDistinctMigrationsHistoryTable()
    {
        var seed = FreshRootSeed();
        using (var host = await StartKeyedHostAsync(seed))
        {
            await host.StopAsync();
        }
        SqliteConnection.ClearAllPools();

        // Open the keyed store directly and confirm BOTH history tables exist —
        // proving the two contexts migrated without clobbering each other.
        var dek = new SqlCipherKeyDerivation().DeriveSqlCipherKey(
            seed, LocalNodeSqlCipherRegistration.RelationalStoreKeyId);
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

        Assert.Contains("__EFMigrationsHistory", historyTables);
        Assert.Contains(NodeLocalMaintenanceDbContext.MigrationsHistoryTableName, historyTables);
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
