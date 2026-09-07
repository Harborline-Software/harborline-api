using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class InstallationFounderBootstrapServiceTests
{
    [Fact]
    public async Task Bootstrap_Creates_Account_Root_Grant_And_Audit_Atomically()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);

        var result = await service.InitializeAsync(Command("founder", "bootstrap-1"));

        Assert.Equal(InstallationFounderBootstrapStatus.Created, result.Status);
        Assert.NotNull(result.InstallationIdentityId);
        Assert.NotNull(result.AccountId);
        Assert.NotNull(result.GrantId);

        await using var context = database.Factory.CreateDbContext();
        var identity = Assert.Single(await context.InstallationIdentities.AsNoTracking().ToArrayAsync());
        Assert.Equal(InstallationIdentityRecord.Revision3AuthorityVersion, identity.AuthorityVersion);
        Assert.Single(await context.Accounts.AsNoTracking().ToArrayAsync());
        Assert.Single(await context.RootKeyEpochs.AsNoTracking().ToArrayAsync());
        Assert.Single(await context.InstallationAccessGrants.AsNoTracking().ToArrayAsync());
        var head = Assert.Single(await context.AuditHeads.AsNoTracking().ToArrayAsync());
        var envelope = Assert.Single(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
        Assert.Equal(head.HeadHash, envelope.EnvelopeHash);
        Assert.Equal(1, head.Sequence);
        Assert.Equal(result.InstallationIdentityId, envelope.InstallationIdentityId);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 21, 0, 0, TimeSpan.Zero), envelope.OccurredAtUtc);
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));
        Assert.DoesNotContain("credential", envelope.EventType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", envelope.EventType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bootstrap_Distinguishes_Idempotent_And_Changed_Replay()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        var original = Command("founder", "bootstrap-1");

        Assert.Equal(
            InstallationFounderBootstrapStatus.Created,
            (await service.InitializeAsync(original)).Status);
        Assert.Equal(
            InstallationFounderBootstrapStatus.IdempotentReplay,
            (await service.InitializeAsync(original)).Status);
        Assert.Equal(
            InstallationFounderBootstrapStatus.ChangedReplay,
            (await service.InitializeAsync(Command("other", "bootstrap-1"))).Status);
        Assert.Equal(
            InstallationFounderBootstrapStatus.AlreadyInitialized,
            (await service.InitializeAsync(Command("other", "bootstrap-2"))).Status);
    }

    [Fact]
    public async Task Bootstrap_Replay_Does_Not_Depend_On_Later_Account_Or_Root_State()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        var original = Command("founder", "bootstrap-1");
        Assert.Equal(
            InstallationFounderBootstrapStatus.Created,
            (await service.InitializeAsync(original)).Status);

        await using (var context = database.Factory.CreateDbContext())
        {
            var account = await context.Accounts.SingleAsync();
            account.NormalizedUsername = "RENAMED";
            account.CredentialCeremonyId = Guid.NewGuid().ToString("N");
            account.CredentialHash = original.CredentialHash.Replace("p=1", "p=2", StringComparison.Ordinal);
            account.SecurityVersion++;
            var root = await context.RootKeyEpochs.SingleAsync();
            root.RootPublicKeyFingerprint = string.Join(":", Enumerable.Repeat("CD", 32));
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            InstallationFounderBootstrapStatus.IdempotentReplay,
            (await service.InitializeAsync(original)).Status);
    }

    [Fact]
    public async Task Bootstrap_Refuses_Tampered_Replay_Evidence()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        var original = Command("founder", "bootstrap-1");
        await service.InitializeAsync(original);

        await using (var context = database.Factory.CreateDbContext())
        {
            var envelope = await context.AuditEnvelopes.SingleAsync();
            envelope.ActorId = "tampered-actor";
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InitializeAsync(original));
        Assert.StartsWith("installation-identity.audit_evidence_invalid:", exception.Message);
    }

    [Fact]
    public async Task Bootstrap_Refuses_Authenticated_NonFounder_Replay_Evidence()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        var original = Command("founder", "bootstrap-1");
        await service.InitializeAsync(original);

        await using (var context = database.Factory.CreateDbContext())
        {
            var envelope = await context.AuditEnvelopes.SingleAsync();
            var head = await context.AuditHeads.SingleAsync();
            envelope.EventType = "InstallationAccountReviewed";
            envelope.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(envelope);
            head.HeadHash = envelope.EnvelopeHash;
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InitializeAsync(original));
        Assert.StartsWith("installation-identity.audit_evidence_invalid:", exception.Message);
    }

    [Fact]
    public async Task One_Hundred_Concurrent_First_Account_Attempts_Have_One_Winner()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            service.InitializeAsync(Command($"founder-{index}", $"bootstrap-{index}"))));

        Assert.Single(results, result => result.Status == InstallationFounderBootstrapStatus.Created);
        Assert.Equal(
            99,
            results.Count(result => result.Status == InstallationFounderBootstrapStatus.AlreadyInitialized));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(1, await context.Accounts.CountAsync());
        Assert.Equal(1, await context.InstallationAccessGrants.CountAsync());
        Assert.Equal(1, await context.AuditEnvelopes.CountAsync());
        Assert.Equal(1, await context.AuditHeads.CountAsync());
    }

    [Fact]
    public async Task Audit_Refusal_Rolls_Back_All_Authority_Rows()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        await using (var setup = database.Factory.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER refuse_installation_audit
                BEFORE INSERT ON installation_audit_envelopes
                BEGIN
                    SELECT RAISE(ABORT, 'audit refused');
                END;
                """);
        }

        var service = CreateService(database.Factory);
        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            service.InitializeAsync(Command("founder", "bootstrap-1")));

        await using var verify = database.Factory.CreateDbContext();
        Assert.Equal(0, await verify.InstallationIdentities.CountAsync());
        Assert.Equal(0, await verify.Accounts.CountAsync());
        Assert.Equal(0, await verify.RootKeyEpochs.CountAsync());
        Assert.Equal(0, await verify.InstallationAccessGrants.CountAsync());
        Assert.Equal(0, await verify.AuditEnvelopes.CountAsync());
        Assert.Equal(0, await verify.AuditHeads.CountAsync());
    }

    [Fact]
    public async Task Audit_Hash_Binds_Actor_Time_And_Root_Evidence()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        await service.InitializeAsync(Command("founder", "bootstrap-1"));

        await using var context = database.Factory.CreateDbContext();
        var envelope = await context.AuditEnvelopes.AsNoTracking().SingleAsync();
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));

        envelope.ActorKind = "tampered";
        Assert.False(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));
        envelope.ActorKind = "local-bootstrap-authority";
        envelope.OccurredAtUtc = envelope.OccurredAtUtc.AddMilliseconds(1);
        Assert.False(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));
        envelope.OccurredAtUtc = envelope.OccurredAtUtc.AddMilliseconds(-1);
        envelope.RootEpoch = 2;
        Assert.False(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));
    }

    [Fact]
    public async Task Invalid_Credential_Or_Root_Artifact_Is_Refused_Before_Write()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        var valid = Command("founder", "bootstrap-1");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.InitializeAsync(valid with { CredentialHash = "not-argon2id" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.InitializeAsync(valid with { RootPublicKeyFingerprint = "not-a-fingerprint" }));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(0, await context.InstallationIdentities.CountAsync());
        Assert.Equal(0, await context.Accounts.CountAsync());
    }

    // earlier repository ticket #3373. Each of these fails if the designation write is removed from the ceremony,
    // and they pin distinct properties rather than restating one another.

    [Fact]
    public async Task Bootstrap_Designates_The_Founder_In_The_Same_Transaction()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);

        var result = await service.InitializeAsync(Command("founder", "bootstrap-1"));

        await using var context = database.Factory.CreateDbContext();
        var designation = Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
        Assert.Equal(
            InstallationIdentityRootDesignationRecord.SingletonKeyValue,
            designation.SingletonKey);
        // The account this ceremony just created IS the founder. That equality is the whole card:
        // membership is derived by comparing a principal's AccountId against this column.
        Assert.Equal(result.AccountId, designation.AccountId);
        Assert.Equal("bootstrap-1", designation.AuditCorrelationId);
        Assert.Equal(1, designation.OwnerVersion);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 21, 0, 0, TimeSpan.Zero), designation.DesignatedAtUtc);
    }

    [Fact]
    public async Task Bootstrap_Designation_Is_Atomic_With_The_Root_Grant()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);

        await service.InitializeAsync(Command("founder", "bootstrap-1"));

        // Same transaction means same account, and it means the audit envelope that attests the
        // ceremony also attests the designation -- a designation committed outside that transaction
        // would reintroduce the partial-activation state the ceremony exists to prevent.
        await using var context = database.Factory.CreateDbContext();
        var grant = Assert.Single(await context.InstallationAccessGrants.AsNoTracking().ToArrayAsync());
        var designation = Assert.Single(await context.RootDesignations.AsNoTracking().ToArrayAsync());
        Assert.Equal(grant.AccountId, designation.AccountId);
        var envelope = Assert.Single(await context.AuditEnvelopes.AsNoTracking().ToArrayAsync());
        var head = Assert.Single(await context.AuditHeads.AsNoTracking().ToArrayAsync());
        Assert.True(InstallationAuditIntegrity.HasValidEnvelopeHash(envelope));
        Assert.Equal(head.HeadHash, envelope.EnvelopeHash);
        // Same ceremony, not merely same database: the designation carries the correlation id the
        // audit envelope was written under, so a designation from any other ceremony would not match.
        Assert.Equal(envelope.CorrelationId, designation.AuditCorrelationId);
    }

    [Fact]
    public async Task Bootstrap_Replay_Does_Not_Duplicate_Or_Overwrite_The_Designation()
    {
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        var command = Command("founder", "bootstrap-1");

        await service.InitializeAsync(command);
        await using (var before = database.Factory.CreateDbContext())
        {
            var first = Assert.Single(await before.RootDesignations.AsNoTracking().ToArrayAsync());
            Assert.Equal(
                InstallationFounderBootstrapStatus.IdempotentReplay,
                (await service.InitializeAsync(command)).Status);
            Assert.Equal(
                InstallationFounderBootstrapStatus.ChangedReplay,
                (await service.InitializeAsync(Command("other", "bootstrap-1"))).Status);

            await using var after = database.Factory.CreateDbContext();
            var second = Assert.Single(await after.RootDesignations.AsNoTracking().ToArrayAsync());
            Assert.Equal(first.DesignationId, second.DesignationId);
            Assert.Equal(first.AccountId, second.AccountId);
            Assert.Equal(first.DesignatedAtUtc, second.DesignatedAtUtc);
        }
    }

    [Fact]
    public async Task An_Install_Bootstrapped_Without_A_Designation_Still_Has_None()
    {
        // The fallback must stay reachable. An install created before this change has no
        // designation, and nothing here backfills one -- replay stays a function of audit
        // evidence, so SelectedSessionMembership.Unresolved remains a state the system can be in
        // rather than a value nothing produces.
        await using var database = await TestIdentityDatabase.CreateAsync();
        var service = CreateService(database.Factory);
        // The SAME command instance -- Command() mints a fresh credential ceremony id per call, so a
        // rebuilt one is a different command fingerprint and would replay as ChangedReplay.
        var command = Command("founder", "bootstrap-1");
        await service.InitializeAsync(command);

        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.RootDesignations.RemoveRange(await seed.RootDesignations.ToArrayAsync());
            await seed.SaveChangesAsync();
        }

        Assert.Equal(
            InstallationFounderBootstrapStatus.IdempotentReplay,
            (await service.InitializeAsync(command)).Status);

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.RootDesignations.AsNoTracking().ToArrayAsync());
    }

    internal static InstallationFounderBootstrapCommand Command(string username, string correlationId) =>
        new(
            username,
            "$argon2id$v=19$m=19456,t=2,p=1$" +
            Convert.ToBase64String(new byte[16]) + "$" +
            Convert.ToBase64String(new byte[32]),
            Guid.NewGuid().ToString("N"),
            string.Join(":", Enumerable.Repeat("AB", 32)),
            correlationId);

    private static InstallationFounderBootstrapService CreateService(IdentityContextFactory factory) =>
        new(factory, new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 13, 21, 0, 0, TimeSpan.Zero)));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    internal sealed class TestIdentityDatabase : IAsyncDisposable
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
                $"harborline-installation-identity-{Guid.NewGuid():N}.db");
            var factory = new IdentityContextFactory(path);
            await using var context = factory.CreateDbContext();
            await context.Database.MigrateAsync();
            return new TestIdentityDatabase(path, factory);
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
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
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }
}
