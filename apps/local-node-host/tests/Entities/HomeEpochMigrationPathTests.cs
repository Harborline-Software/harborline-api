using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Proves the production <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> path and the
/// runtime model agree on the home-authority fence schema. EnsureCreated-only evidence is insufficient
/// because production replays committed migrations on fresh and upgraded nodes.
/// </summary>
public sealed class HomeEpochMigrationPathTests : IAsyncLifetime
{
    private const string Tenant = "tenant-home-epoch-migration";
    private string _directory = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-home-epoch-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup only.
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task MigrateAsync_Creates_Complete_HomeEpoch_Schema_Without_Pending_Model_Changes()
    {
        var path = Path.Combine(_directory, "migrated.db");
        var factory = CreateFactory(path);
        await using var context = await factory.CreateDbContextAsync();

        await context.Database.MigrateAsync();

        Assert.Contains(
            await context.Database.GetAppliedMigrationsAsync(),
            migration => migration.EndsWith("AddHomeEpochTable", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());

        var schema = await ReadSchemaAsync(path);
        Assert.Equal(
            [
                new Column("CoApproverIssuerId", "TEXT", false, 0),
                new Column("CoApproverSignature", "TEXT", false, 0),
                new Column("EpochNumber", "INTEGER", true, 2),
                new Column("HomeDeviceId", "TEXT", true, 0),
                new Column("IssuedAt", "TEXT", true, 0),
                new Column("IssuerId", "TEXT", true, 0),
                new Column("Nonce", "TEXT", true, 0),
                new Column("PreviousEpochNumber", "INTEGER", true, 0),
                new Column("PromotionKind", "TEXT", true, 0),
                new Column("Signature", "TEXT", true, 0),
                new Column("TenantId", "TEXT", true, 1),
            ],
            schema.Columns);
        Assert.Equal(["ix_home_epochs_tenant_epoch"], schema.Indexes);
    }

    [Fact]
    public async Task MigrateAsync_And_EnsureCreated_Produce_Equivalent_HomeEpoch_Schema()
    {
        var migratedPath = Path.Combine(_directory, "migrated-parity.db");
        var createdPath = Path.Combine(_directory, "created-parity.db");

        await using (var migrated = await CreateFactory(migratedPath).CreateDbContextAsync())
        {
            await migrated.Database.MigrateAsync();
        }
        await using (var created = await CreateFactory(createdPath).CreateDbContextAsync())
        {
            await created.Database.EnsureCreatedAsync();
        }

        var createdSchema = await ReadSchemaAsync(createdPath);
        var migratedSchema = await ReadSchemaAsync(migratedPath);
        Assert.Equal(createdSchema.Columns, migratedSchema.Columns);
        Assert.Equal(createdSchema.Indexes, migratedSchema.Indexes);
    }

    [Fact]
    public async Task HomeEpochStore_RoundTrips_A_Signed_Bump_After_MigrateAsync()
    {
        var path = Path.Combine(_directory, "store.db");
        var factory = CreateFactory(path);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
        }

        using var keyPair = KeyPair.Generate();
        var payload = HomeEpochSignaturePayload.For(
            Tenant,
            epochNumber: 1,
            previousEpochNumber: 0,
            homeDeviceId: "device-a",
            promotionKind: HomePromotionKind.PlannedHandoff);
        var issuedAt = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid();
        var signed = await new Ed25519Signer(keyPair).SignAsync(payload, issuedAt, nonce);
        var store = new HomeEfHomeEpochStore(factory, new Ed25519Verifier());

        await store.AdvanceAsync(new HomeEpochRecord
        {
            TenantId = Tenant,
            EpochNumber = 1,
            PreviousEpochNumber = 0,
            HomeDeviceId = "device-a",
            PromotionKind = HomePromotionKind.PlannedHandoff,
            IssuedAt = issuedAt,
            Nonce = nonce,
            IssuerId = keyPair.PrincipalId.ToBase64Url(),
            Signature = signed.Signature.ToBase64Url(),
        });

        var current = await store.GetCurrentEpochAsync(Tenant);
        Assert.NotNull(current);
        Assert.Equal(1, current.EpochNumber);
        Assert.Equal("device-a", current.HomeDeviceId);
    }

    private static IDbContextFactory<LocalNodeDbContext> CreateFactory(string databasePath)
    {
        var services = new ServiceCollection();
        foreach (var module in DesignTimeLocalNodeDbContextFactory.CreateMigrationModules())
        {
            services.AddSingleton(module);
        }
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        return services.BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
    }

    private static async Task<TableSchema> ReadSchemaAsync(string databasePath)
    {
        var columns = new List<Column>();
        var indexes = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(home_epochs);";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(new Column(
                    reader.GetString(reader.GetOrdinal("name")),
                    reader.GetString(reader.GetOrdinal("type")),
                    reader.GetInt64(reader.GetOrdinal("notnull")) == 1,
                    checked((int)reader.GetInt64(reader.GetOrdinal("pk")))));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA index_list(home_epochs);";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(reader.GetOrdinal("name"));
                if (!name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal))
                {
                    indexes.Add(name);
                }
            }
        }

        return new TableSchema(
            columns.OrderBy(column => column.Name, StringComparer.Ordinal).ToArray(),
            indexes.Order(StringComparer.Ordinal).ToArray());
    }

    private sealed record Column(string Name, string Type, bool Required, int PrimaryKeyOrder);
    private sealed record TableSchema(Column[] Columns, string[] Indexes);
}
