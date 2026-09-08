using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class AccountSetupInvitationStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 16, 15, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "INV-01B")]
    public async Task Issue_Persists_Only_Digest_Then_Consumes_Exactly_Once_With_Purpose_Fence()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new AccountSetupInvitationStore(database.Factory);

        var issued = await store.IssueAsync(Seed("command-1", Now + TimeSpan.FromHours(1)));

        Assert.NotNull(issued);
        await using (var context = database.Factory.CreateDbContext())
        {
            var row = await context.AccountSetupInvitations.AsNoTracking().SingleAsync();
            Assert.Equal(AccountSetupInvitationStore.Digest(issued!.RawCode), row.TokenDigest);
            Assert.DoesNotContain(issued.RawCode, row.TokenDigest, StringComparison.Ordinal);
            Assert.Equal(WebSetupInvitationPurpose.AccountSetup, row.Purpose);
            Assert.Empty(await context.Accounts.AsNoTracking().ToArrayAsync());
            Assert.DoesNotContain(
                context.Model.FindEntityType(typeof(AccountSetupInvitationRecord))!.GetProperties(),
                property => property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
        }

        Assert.False(await store.ConsumeAsync(
            "wrong-key", TenantId, WebSetupInvitationPurpose.AccountSetup, Now));
        Assert.False(await store.ConsumeAsync(
            issued!.RawCode, "99999999-9999-9999-9999-999999999999",
            WebSetupInvitationPurpose.AccountSetup, Now));
        Assert.False(await store.ConsumeAsync(
            issued.RawCode, TenantId, WebSetupInvitationPurpose.MembershipSetup, Now));
        Assert.True(await store.ConsumeAsync(
            issued.RawCode, TenantId, WebSetupInvitationPurpose.AccountSetup, Now));
        Assert.False(await store.ConsumeAsync(
            issued.RawCode, TenantId, WebSetupInvitationPurpose.AccountSetup, Now));
    }

    [Fact]
    [Trait("PlanCard", "INV-01B")]
    public async Task Expired_And_Revoked_Codes_Refuse_Without_Leaking_Raw_Material()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new AccountSetupInvitationStore(database.Factory);
        var expired = await store.IssueAsync(Seed("expired", Now + TimeSpan.FromMinutes(1)));
        var revoked = await store.IssueAsync(Seed("revoked", Now + TimeSpan.FromHours(1)));

        Assert.NotNull(expired);
        Assert.NotNull(revoked);
        Assert.False(await store.ConsumeAsync(
            expired!.RawCode,
            TenantId,
            WebSetupInvitationPurpose.AccountSetup,
            expired.AbsoluteExpiresAtUtc));
        Assert.True(await store.RevokeAsync(revoked!.InvitationId, TenantId, Now));
        Assert.False(await store.RevokeAsync(revoked.InvitationId, TenantId, Now));
        Assert.False(await store.ConsumeAsync(
            revoked.RawCode,
            TenantId,
            WebSetupInvitationPurpose.AccountSetup,
            Now));
    }

    [Fact]
    [Trait("PlanCard", "INV-01A")]
    public async Task Migration_Upgrades_Restarts_And_Has_No_Pending_Model()
    {
        var path = Path.Combine(Path.GetTempPath(), $"account-setup-migration-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new ContextFactory(path);
            await using (var upgrading = factory.CreateDbContext())
            {
                await upgrading.GetService<IMigrator>().MigrateAsync(
                    "20260718132100_LegacyRenameCheckpointBinding");
                await upgrading.Database.MigrateAsync();
                Assert.False(upgrading.Database.HasPendingModelChanges());
                Assert.Equal(8, (await upgrading.Database.GetAppliedMigrationsAsync()).Count());
            }

            await using var restarted = factory.CreateDbContext();
            await restarted.Database.MigrateAsync();
            Assert.False(restarted.Database.HasPendingModelChanges());
            Assert.Empty(await restarted.AccountSetupInvitations.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2621")]
    public async Task Issue_Emits_Exactly_One_InvitationIssued_Audit_Envelope()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new AccountSetupInvitationStore(database.Factory);

        var issued = await store.IssueAsync(Seed("issue-audit", Now + TimeSpan.FromHours(1)));

        Assert.NotNull(issued);
        await using var context = database.Factory.CreateDbContext();
        var envelope = Assert.Single(await context.AuditEnvelopes.AsNoTracking()
            .Where(item => item.EventType == InstallationIdentityAuditEventTypes.InvitationIssued)
            .ToArrayAsync());
        Assert.Equal("installation-account", envelope.ActorKind);
        Assert.Equal("account-admin", envelope.ActorId);
        Assert.Equal(2, envelope.Sequence); // genesis is sequence 1
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));

        var head = await context.AuditHeads.AsNoTracking().SingleAsync();
        Assert.Equal(2, head.Sequence);
        Assert.Equal(envelope.EnvelopeHash, head.HeadHash);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2621")]
    public async Task Consume_Emits_Exactly_One_InvitationAccepted_Audit_Envelope()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new AccountSetupInvitationStore(database.Factory);
        var issued = await store.IssueAsync(Seed("consume-audit", Now + TimeSpan.FromHours(1)));
        Assert.NotNull(issued);

        Assert.True(await store.ConsumeAsync(
            issued!.RawCode, TenantId, WebSetupInvitationPurpose.AccountSetup, Now));
        // A repeated consume is a no-op and must NOT append a second acceptance envelope.
        Assert.False(await store.ConsumeAsync(
            issued.RawCode, TenantId, WebSetupInvitationPurpose.AccountSetup, Now));

        await using var context = database.Factory.CreateDbContext();
        var accepted = Assert.Single(await context.AuditEnvelopes.AsNoTracking()
            .Where(item => item.EventType == InstallationIdentityAuditEventTypes.InvitationAccepted)
            .ToArrayAsync());
        Assert.Equal("installation-invitation", accepted.ActorKind);
        Assert.Equal(issued.InvitationId, accepted.ActorId);
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(accepted));

        // Full chain: genesis + issued + accepted, still hash-valid.
        var chain = await context.AuditEnvelopes.AsNoTracking()
            .OrderBy(item => item.Sequence).ToArrayAsync();
        var head = await context.AuditHeads.AsNoTracking().SingleAsync();
        Assert.Equal(3, chain.Length);
        Assert.True(InstallationAuditIntegrity.HasValidChain(
            chain, head, InstallationAuditTestGenesis.InstallationIdentityId));
    }

    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static AccountSetupInvitationSeed Seed(string fingerprint, DateTimeOffset expiresAt) =>
        new(
            TenantId,
            "account-admin",
            "principal-admin",
            "party-admin",
            "session-admin",
            "membership-admin",
            3,
            "22222222-2222-2222-2222-222222222222",
            4,
            5,
            "[\"records:read\"]",
            fingerprint,
            Now,
            expiresAt);

    private sealed class TestDatabase(string path, ContextFactory factory) : IAsyncDisposable
    {
        public ContextFactory Factory { get; } = factory;

        public static async Task<TestDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"account-setup-store-{Guid.NewGuid():N}.db");
            var factory = new ContextFactory(path);
            await using var context = factory.CreateDbContext();
            await context.Database.MigrateAsync();
            await InstallationAuditTestGenesis.SeedAsync(context);
            return new TestDatabase(path, factory);
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ContextFactory(string path)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }
}
