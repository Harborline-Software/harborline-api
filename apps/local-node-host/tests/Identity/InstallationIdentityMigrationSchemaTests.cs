using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MIG-01A proves only dormant schema. No lease, barrier, copy, verification, or marker mutation
/// behavior is introduced by this card.
/// </summary>
[Trait("PlanCard", "MIG-01A")]
public sealed class InstallationIdentityMigrationSchemaTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260713233947_InstallationIdentityCoordination";
    private string _directory = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"harborline-mig-01a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
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
    public void Model_Contains_The_Bounded_Dormant_Cutover_Record_Set()
    {
        using var context = CreateContext(Path.Combine(_directory, "model.db"));

        AssertSingleton<InstallationIdentityMigrationLeaseRecord>(context.Model);
        AssertSingleton<InstallationIdentityRootDesignationRecord>(context.Model);
        AssertSingleton<InstallationIdentityCutoverStateRecord>(context.Model);

        var watermark = RequiredEntity<InstallationIdentityMigrationSourceWatermarkRecord>(context.Model);
        Assert.Equal(
            [nameof(InstallationIdentityMigrationSourceWatermarkRecord.SourceKind),
                nameof(InstallationIdentityMigrationSourceWatermarkRecord.SourcePartition)],
            watermark.FindPrimaryKey()!.Properties.Select(property => property.Name));

        var collision = RequiredEntity<InstallationIdentityMigrationCollisionRecord>(context.Model);
        Assert.True(collision.FindProperty(nameof(InstallationIdentityMigrationCollisionRecord.OwnerVersion))!
            .IsConcurrencyToken);
        Assert.All(
            new[]
            {
                nameof(InstallationIdentityMigrationCollisionRecord.RepairCommandFingerprint),
                nameof(InstallationIdentityMigrationCollisionRecord.RepairSourceKeyDigest),
                nameof(InstallationIdentityMigrationCollisionRecord.RepairTargetDigest),
                nameof(InstallationIdentityMigrationCollisionRecord.ExpectedOwnerVersion),
                nameof(InstallationIdentityMigrationCollisionRecord.RecoveryActorId),
                nameof(InstallationIdentityMigrationCollisionRecord.RecoveryRootEpoch),
                nameof(InstallationIdentityMigrationCollisionRecord.RecoveryRootPublicKeyFingerprint),
            },
            property => Assert.NotNull(collision.FindProperty(property)));
        Assert.True(Assert.Single(
            collision.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(InstallationIdentityMigrationCollisionRecord.CollisionKeyDigest)])).IsUnique);

        var tombstone = RequiredEntity<InstallationIdentityMigrationTombstoneRecord>(context.Model);
        Assert.True(Assert.Single(
            tombstone.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(InstallationIdentityMigrationTombstoneRecord.SourceKind),
                    nameof(InstallationIdentityMigrationTombstoneRecord.SourceKeyDigest)])).IsUnique);

        var classifiedSafeTypes = new[] { collision, tombstone,
            RequiredEntity<InstallationIdentityRootDesignationRecord>(context.Model) };
        Assert.All(classifiedSafeTypes, entity => Assert.DoesNotContain(
            entity.GetProperties(),
            property => property.Name.Contains("Username", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Party", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Fresh_Migration_Seeds_V1_Marker_And_Survives_Restart()
    {
        var path = Path.Combine(_directory, "fresh.db");
        await using (var context = CreateContext(path))
        {
            await context.Database.MigrateAsync();

            Assert.Contains(
                await context.Database.GetAppliedMigrationsAsync(),
                migration => migration.EndsWith("InstallationIdentityCutoverRecords", StringComparison.Ordinal));
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());

            AssertV1Authoritative(await context.CutoverStates.AsNoTracking().SingleAsync());
            var tables = await ReadTablesAsync(path);
            Assert.All(
                new[]
                {
                    "installation_identity_migration_lease",
                    "installation_identity_migration_source_watermarks",
                    "installation_identity_migration_collisions",
                    "installation_identity_migration_tombstones",
                    "installation_identity_root_designation",
                    "installation_identity_cutover_state",
                },
                expected => Assert.Contains(expected, tables, StringComparer.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        await using var restarted = CreateContext(path);
        AssertV1Authoritative(await restarted.CutoverStates.AsNoTracking().SingleAsync());
        Assert.Empty(await restarted.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Upgrade_Preserves_V1_Data_And_Leaves_Authority_Unchanged()
    {
        var path = Path.Combine(_directory, "upgrade.db");
        await using var context = CreateContext(path);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO installation_accounts
                (account_id, normalized_username, credential_hash, credential_algorithm,
                 credential_ceremony_id, credential_version, status, security_version,
                 owner_version, created_at_utc, updated_at_utc)
            VALUES
                ('legacy-account', 'LEGACY', 'synthetic-hash', 'test', 'migration-test',
                 1, 'Active', 1, 1, 0, 0);
            """);

        await context.Database.MigrateAsync();

        Assert.True(await context.Accounts.AsNoTracking().AnyAsync(row => row.AccountId == "legacy-account"));
        AssertV1Authoritative(await context.CutoverStates.AsNoTracking().SingleAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Cutover_State_Has_One_Production_Orchestrator_Owner()
    {
        var hostRoot = FindLocalNodeHostRoot();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(hostRoot, "Data", "Identity", "InstallationIdentityRecords.cs"),
            Path.Combine(hostRoot, "Data", "Identity", "NodeLocalInstallationIdentityDbContext.cs"),
        };
        var consumers = Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !allowed.Contains(path))
            .Where(path => File.ReadAllText(path).Contains(
                nameof(InstallationIdentityCutoverStateRecord), StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            [Path.Combine(hostRoot, "Data", "Identity", "InstallationIdentityCutoverOrchestrator.cs")],
            consumers);
    }

    private static void AssertSingleton<T>(IModel model) where T : class
    {
        var entity = RequiredEntity<T>(model);
        Assert.Single(entity.FindPrimaryKey()!.Properties);
        Assert.Equal("SingletonKey", entity.FindPrimaryKey()!.Properties[0].Name);
    }

    private static void AssertV1Authoritative(InstallationIdentityCutoverStateRecord marker)
    {
        Assert.Equal(InstallationIdentityCutoverStateRecord.SingletonKeyValue, marker.SingletonKey);
        Assert.Equal(InstallationIdentityCutoverStage.LegacyV1Authoritative, marker.Stage);
        Assert.Equal(InstallationIdentityCutoverStateRecord.LegacyV1AuthorityVersion, marker.AuthorityVersion);
        Assert.Equal(0, marker.V1WriteBarrierVersion);
        Assert.Null(marker.MigrationRunId);
        Assert.Null(marker.SourceWatermarkDigest);
        Assert.Null(marker.FinalVerificationDigest);
        Assert.Null(marker.CommittedAtUtc);
    }

    private static IEntityType RequiredEntity<T>(IModel model) where T : class =>
        model.FindEntityType(typeof(T))
        ?? throw new InvalidOperationException($"Missing entity model for {typeof(T).Name}.");

    private static NodeLocalInstallationIdentityDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
            .Options;
        return new NodeLocalInstallationIdentityDbContext(options);
    }

    private static async Task<string[]> ReadTablesAsync(string databasePath)
    {
        var tables = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private static string FindLocalNodeHostRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "local-node-host");
            if (File.Exists(Path.Combine(candidate, "Harborline.LocalNodeHost.csproj")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate apps/local-node-host from test output.");
    }
}
