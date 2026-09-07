using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

[Trait("PlanCard", "MTW-2-2604")]
public sealed class InstallationFounderBindingServiceTests
{
    [Fact]
    public async Task Same_Idempotency_Key_Returns_The_Completed_Receipt_And_Writes_One_Row()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        var service = CreateService(database.Factory);
        var command = Command("founder-account", "bind-attempt-1");

        var first = await service.BindAsync(command);
        var second = await service.BindAsync(command);

        Assert.Equal(InstallationFounderBindingStatus.Created, first.Status);
        Assert.Equal(InstallationFounderBindingStatus.IdempotentReplay, second.Status);
        Assert.NotNull(first.Receipt);
        Assert.Equal(first.Receipt, second.Receipt);

        await using var context = database.Factory.CreateDbContext();
        var row = Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
        Assert.Equal(first.Receipt!.DesignationId, row.DesignationId);
        Assert.Equal("founder-account", row.AccountId);
        Assert.DoesNotContain(command.IdempotencyKey, row.IdempotencyKeyDigest, StringComparison.Ordinal);
        Assert.Equal(64, row.IdempotencyKeyDigest.Length);
    }

    [Fact]
    public async Task Concurrent_Same_Command_Has_One_Winner_And_Loser_Receives_Winner_Receipt()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        var command = Command("founder-account", "concurrent-bind-attempt");

        var results = await Task.WhenAll(
            CreateService(database.Factory).BindAsync(command),
            CreateService(database.Factory).BindAsync(command));

        var winner = Assert.Single(
            results,
            result => result.Status == InstallationFounderBindingStatus.Created);
        var loser = Assert.Single(
            results,
            result => result.Status == InstallationFounderBindingStatus.IdempotentReplay);
        Assert.NotNull(winner.Receipt);
        Assert.Equal(winner.Receipt, loser.Receipt);

        await using var context = database.Factory.CreateDbContext();
        var row = Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
        Assert.Equal(winner.Receipt!.DesignationId, row.DesignationId);
        Assert.Equal(winner.Receipt.DesignatedAtUtc, row.DesignatedAtUtc);
    }

    [Fact]
    public async Task Locator_Finds_A_Completed_Receipt_After_Restart()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        var command = Command("founder-account", "restart-safe-attempt");
        var created = await CreateService(database.Factory).BindAsync(command);

        var restartedLocator = new InstallationFounderCompletedReceiptLocator(
            new IdentityContextFactory(database.Path));
        var located = await restartedLocator.FindAsync(command.IdempotencyKey);

        Assert.Equal(created.Receipt, located);
    }

    [Fact]
    public async Task Same_Key_With_Changed_Founder_Evidence_Is_Refused_Without_A_Second_Row()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        await database.AddAccountAsync("other-account");
        var service = CreateService(database.Factory);
        var original = Command("founder-account", "same-key");
        var changed = Command("other-account", "same-key") with
        {
            FounderBinding = Binding("founder-tenant", "other-principal", "other-party"),
        };

        var first = await service.BindAsync(original);
        var replay = await service.BindAsync(changed);

        Assert.Equal(InstallationFounderBindingStatus.Created, first.Status);
        Assert.Equal(InstallationFounderBindingStatus.ChangedReplay, replay.Status);
        Assert.Null(replay.Receipt);
        await using var context = database.Factory.CreateDbContext();
        Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("other-tenant", "founder-principal", "founder-party")]
    [InlineData("founder-tenant", "other-principal", "founder-party")]
    [InlineData("founder-tenant", "founder-principal", "other-party")]
    public async Task Same_Key_With_Only_One_Changed_Binding_Coordinate_Is_Refused(
        string tenant,
        string principal,
        string party)
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        var service = CreateService(database.Factory);
        var original = Command("founder-account", "binding-coordinate-replay");
        var changed = original with
        {
            FounderBinding = Binding(tenant, principal, party),
        };

        var first = await service.BindAsync(original);
        var replay = await service.BindAsync(changed);

        Assert.Equal(InstallationFounderBindingStatus.Created, first.Status);
        Assert.Equal(InstallationFounderBindingStatus.ChangedReplay, replay.Status);
        Assert.Null(replay.Receipt);
        await using var context = database.Factory.CreateDbContext();
        var row = Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
        Assert.Equal(first.Receipt!.SourceCompositeKeyDigest, row.SourceCompositeKeyDigest);
    }

    [Fact]
    public async Task Same_Key_With_Only_Changed_Expected_Source_Version_Is_Refused()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        var service = CreateService(database.Factory);
        var original = Command("founder-account", "source-version-replay");
        var changed = original with
        {
            ExpectedSourceVersion = original.ExpectedSourceVersion + 1,
        };

        var first = await service.BindAsync(original);
        var replay = await service.BindAsync(changed);

        Assert.Equal(InstallationFounderBindingStatus.Created, first.Status);
        Assert.Equal(InstallationFounderBindingStatus.ChangedReplay, replay.Status);
        Assert.Null(replay.Receipt);
        await using var context = database.Factory.CreateDbContext();
        var row = Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
        Assert.Equal(first.Receipt!.ExpectedSourceVersion, row.ExpectedSourceVersion);
    }

    [Fact]
    public async Task Different_Key_Cannot_Replace_The_Existing_Designation()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        var service = CreateService(database.Factory);

        var first = await service.BindAsync(Command("founder-account", "first-key"));
        var second = await service.BindAsync(Command("founder-account", "different-key"));

        Assert.Equal(InstallationFounderBindingStatus.Created, first.Status);
        Assert.Equal(InstallationFounderBindingStatus.AlreadyDesignated, second.Status);
        Assert.Null(second.Receipt);
        await using var context = database.Factory.CreateDbContext();
        Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Binding_Requires_An_Existing_Active_Installation_Account()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BindAsync(Command("missing-account", "missing")));
        Assert.Contains("founder_account_not_active", missing.Message, StringComparison.Ordinal);

        await database.AddAccountAsync("disabled-account", InstallationAccountStatus.Disabled);
        var disabled = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BindAsync(Command("disabled-account", "disabled")));
        Assert.Contains("founder_account_not_active", disabled.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2621")]
    public async Task Bind_Emits_Exactly_One_FounderBindingDesignated_Audit_Envelope()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await database.AddAccountAsync("founder-account");
        var service = CreateService(database.Factory);
        var command = Command("founder-account", "audit-bind");

        var first = await service.BindAsync(command);
        // Idempotent replay must NOT append a second designation envelope.
        var replay = await service.BindAsync(command);

        Assert.Equal(InstallationFounderBindingStatus.Created, first.Status);
        Assert.Equal(InstallationFounderBindingStatus.IdempotentReplay, replay.Status);

        await using var context = database.Factory.CreateDbContext();
        var envelope = Assert.Single(await context.AuditEnvelopes.AsNoTracking()
            .Where(item =>
                item.EventType == InstallationIdentityAuditEventTypes.FounderBindingDesignated)
            .ToArrayAsync());
        Assert.Equal("installation-account", envelope.ActorKind);
        Assert.Equal("founder-account", envelope.ActorId);
        Assert.Equal(2, envelope.Sequence); // genesis is sequence 1
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));

        var chain = await context.AuditEnvelopes.AsNoTracking()
            .OrderBy(item => item.Sequence).ToArrayAsync();
        var head = await context.AuditHeads.AsNoTracking().SingleAsync();
        Assert.True(InstallationAuditIntegrity.HasValidChain(
            chain, head, InstallationAuditTestGenesis.InstallationIdentityId));
    }

    private static InstallationFounderBindingService CreateService(IdentityContextFactory factory) =>
        new(factory, new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 16, 18, 30, 0, TimeSpan.Zero)));

    private static InstallationFounderBindingCommand Command(string accountId, string idempotencyKey) =>
        new(
            accountId,
            Binding("founder-tenant", "founder-principal", "founder-party"),
            ExpectedSourceVersion: 7,
            idempotencyKey,
            $"audit-{idempotencyKey}");

    private static CanonicalPartyBinding Binding(string tenant, string principal, string party) =>
        new(
            new TenantId(tenant),
            new PrincipalUserId(principal),
            new CanonicalPartyReference(party));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestIdentityDatabase : IAsyncDisposable
    {
        private TestIdentityDatabase(string path, IdentityContextFactory factory)
        {
            Path = path;
            Factory = factory;
        }

        public string Path { get; }

        public IdentityContextFactory Factory { get; }

        public static async Task<TestIdentityDatabase> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"harborline-founder-binding-{Guid.NewGuid():N}.db");
            var factory = new IdentityContextFactory(path);
            await using var context = factory.CreateDbContext();
            await context.Database.MigrateAsync();
            await InstallationAuditTestGenesis.SeedAsync(context);
            return new TestIdentityDatabase(path, factory);
        }

        public async Task AddAccountAsync(
            string accountId,
            InstallationAccountStatus status = InstallationAccountStatus.Active)
        {
            await using var context = Factory.CreateDbContext();
            context.Accounts.Add(new InstallationAccountRecord
            {
                AccountId = accountId,
                NormalizedUsername = accountId.ToUpperInvariant(),
                CredentialHash = "synthetic-test-hash",
                CredentialAlgorithm = "test",
                CredentialCeremonyId = $"ceremony-{accountId}",
                CredentialVersion = 1,
                Status = status,
                SecurityVersion = 1,
                OwnerVersion = 1,
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
                UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            });
            await context.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // Best-effort test cleanup only.
            }

            return ValueTask.CompletedTask;
        }
    }

    public sealed class IdentityContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False;Default Timeout=30", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }
}
