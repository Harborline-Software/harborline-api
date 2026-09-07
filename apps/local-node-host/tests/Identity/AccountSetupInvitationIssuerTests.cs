using Harborline.Api.Blocks.AccessGrant;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class AccountSetupInvitationIssuerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 16, 20, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvitationSites_RecordTheGateDecisionWithRosterInputs(bool recovery, bool allowed)
    {
        using var capture = new RosterDecisionCapture();
        await using var fixture = await IssueFixture.CreateAsync(allowed ? PermissionCompositions.Admin : PermissionCompositions.Member, capture.Audit);
        if (recovery)
        {
            var issuer = new RecoveryInvitationIssuer(fixture.SessionFactory,
                new WebSelectedSessionStore(fixture.SessionFactory), fixture.IdentityFactory,
                fixture.GrantFactory, new FixedPartyReader("party-admin"), new FixedRosterReader(fixture.Roster),
                new RecoveryInvitationStore(fixture.IdentityFactory), TestAuthorization.AllowGate(), capture.Audit);
            var result = await issuer.IssueAsync(fixture.SelectedHandle,
                new RecoveryInvitationIssueRequest(fixture.TenantId, "ADMIN", "roster-evidence"),
                new AuthorizationWriteContext(new ActorId("principal-admin"), new TenantId(fixture.TenantId), Now));
            Assert.Equal(allowed, result is not null);
        }
        else
        {
            var result = await fixture.Issuer.IssueAsync(fixture.SelectedHandle,
                Request(fixture.TenantId, ["records:read"], "roster-evidence"));
            Assert.Equal(allowed, result is not null);
        }
        var evidence = capture.AssertSingle(allowed);
        Assert.True(evidence.Roster!.Member);
        Assert.False(evidence.Roster.Ejected);
        Assert.Equal("party-admin", evidence.Roster.PartyId);
        await capture.AssertAuditAsync(new TenantId(fixture.TenantId));
    }

    [Fact]
    [Trait("PlanCard", "INV-02")]
    public async Task Selected_Admin_Issues_DigestOnly_AccountSetup_Without_Admission_Or_Reservation()
    {
        await using var fixture = await IssueFixture.CreateAsync(PermissionCompositions.Admin);
        var beforeRoster = fixture.Roster.Members.Count;

        var result = await fixture.Issuer.IssueAsync(
            fixture.SelectedHandle,
            Request(fixture.TenantId, PermissionCompositions.Member.Permissions, "invite-bob"));

        Assert.NotNull(result);
        Assert.Equal(fixture.TenantId, result!.TenantId);
        Assert.Equal(Now + AccountSetupInvitationIssuer.InvitationLifetime, result.AbsoluteExpiresAtUtc);
        Assert.Equal(beforeRoster, fixture.Roster.Members.Count);
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        var row = await identity.AccountSetupInvitations.AsNoTracking().SingleAsync();
        Assert.Equal("account-admin", row.InviterAccountId);
        Assert.Equal("principal-admin", row.InviterPrincipalId);
        Assert.Equal("party-admin", row.InviterPartyId);
        Assert.Equal("membership-admin", row.InviterMembershipId);
        Assert.Equal(3, row.InviterMembershipOwnerVersion);
        Assert.Equal(4, row.InviterGrantOwnerVersion);
        Assert.Equal(5, row.InviterAuthorizationEpoch);
        Assert.Equal(AccountSetupInvitationStore.Digest(result.RawCode), row.TokenDigest);
        Assert.Equal(WebSetupInvitationPurpose.AccountSetup, row.Purpose);
        Assert.Single(await identity.Accounts.AsNoTracking().ToArrayAsync());
        Assert.DoesNotContain("username", row.RequestedPermissionsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("PlanCard", "INV-02")]
    public async Task Missing_MembersManage_And_Privilege_Expansion_Refuse_Without_Durable_Oracle()
    {
        await using (var member = await IssueFixture.CreateAsync(PermissionCompositions.Member))
        {
            Assert.Null(await member.Issuer.IssueAsync(
                member.SelectedHandle,
                Request(member.TenantId, PermissionCompositions.Viewer.Permissions, "unauthorized")));
            await AssertNoInvitationsAsync(member.IdentityFactory);
        }

        await using (var admin = await IssueFixture.CreateAsync(PermissionCompositions.Admin))
        {
            Assert.Null(await admin.Issuer.IssueAsync(
                admin.SelectedHandle,
                Request(admin.TenantId, [Permission.GrantPermissions], "escalated")));
            await AssertNoInvitationsAsync(admin.IdentityFactory);
        }
    }

    [Fact]
    [Trait("PlanCard", "INV-02")]
    public async Task CrossTenant_UnknownSession_And_StaleGrant_Refuse_Without_Write()
    {
        await using var fixture = await IssueFixture.CreateAsync(PermissionCompositions.Admin);
        var otherTenant = "99999999-9999-9999-9999-999999999999";
        Assert.Null(await fixture.Issuer.IssueAsync(
            fixture.SelectedHandle,
            Request(otherTenant, PermissionCompositions.Viewer.Permissions, "cross-tenant")));
        Assert.Null(await fixture.Issuer.IssueAsync(
            "unknown-selected-session",
            Request(fixture.TenantId, PermissionCompositions.Viewer.Permissions, "unknown")));

        await using (var grants = fixture.GrantFactory.CreateDbContext())
        {
            var grant = await grants.Grants.SingleAsync();
            grant.OwnerVersion++;
            await grants.SaveChangesAsync();
        }
        Assert.Null(await fixture.Issuer.IssueAsync(
            fixture.SelectedHandle,
            Request(fixture.TenantId, PermissionCompositions.Viewer.Permissions, "stale")));
        await AssertNoInvitationsAsync(fixture.IdentityFactory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdentityCeremony_PreservesPinnedGrantEpochAndVersionChecksAfterGate(bool staleOwnerVersion)
    {
        await using var fixture = await IssueFixture.CreateAsync(PermissionCompositions.Admin);
        await using (var grants = fixture.GrantFactory.CreateDbContext())
        {
            if (staleOwnerVersion)
            {
                (await grants.Grants.SingleAsync()).OwnerVersion++;
            }
            else
            {
                (await grants.GrantAuthorizationEpochs.SingleAsync()).AuthorizationEpoch++;
            }
            await grants.SaveChangesAsync();
        }

        var result = await fixture.Issuer.IssueAsync(
            fixture.SelectedHandle,
            Request(fixture.TenantId, PermissionCompositions.Viewer.Permissions,
                staleOwnerVersion ? "stale-owner" : "stale-epoch"));

        Assert.Null(result); // the fixture's gate Allowed; the pinned freshness fence still refuses the act.
        await AssertNoInvitationsAsync(fixture.IdentityFactory);
    }

    private static AccountSetupInvitationIssueRequest Request(
        string tenantId,
        IReadOnlyCollection<string> permissions,
        string idempotencyKey) =>
        new(tenantId, permissions, idempotencyKey);

    private static async Task AssertNoInvitationsAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> factory)
    {
        await using var identity = await factory.CreateDbContextAsync();
        Assert.Empty(await identity.AccountSetupInvitations.AsNoTracking().ToArrayAsync());
    }

    private sealed class IssueFixture : IAsyncDisposable
    {
        private readonly string[] _paths;

        private IssueFixture(
            string[] paths,
            ContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
            ContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
            ContextFactory<NodeLocalSearchDbContext> grantFactory,
            string tenantId,
            string selectedHandle,
            MemberRoster roster,
            AccountSetupInvitationIssuer issuer)
        {
            _paths = paths;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            GrantFactory = grantFactory;
            TenantId = tenantId;
            SelectedHandle = selectedHandle;
            Roster = roster;
            Issuer = issuer;
        }

        public ContextFactory<NodeLocalInstallationIdentityDbContext> IdentityFactory { get; }
        public ContextFactory<NodeLocalWebSessionDbContext> SessionFactory { get; }
        public ContextFactory<NodeLocalSearchDbContext> GrantFactory { get; }
        public string TenantId { get; }
        public string SelectedHandle { get; }
        public MemberRoster Roster { get; }
        public AccountSetupInvitationIssuer Issuer { get; }

        public static async Task<IssueFixture> CreateAsync(PermissionSet inviterPermissions, AuthorizationRefusalAudit? refusalAudit = null)
        {
            var tenantId = "11111111-1111-1111-1111-111111111111";
            var identityPath = TempPath("identity");
            var sessionPath = TempPath("session");
            var grantPath = TempPath("grant");
            var identityFactory = ContextFactory<NodeLocalInstallationIdentityDbContext>.Create(
                identityPath,
                options => new NodeLocalInstallationIdentityDbContext(options),
                NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName);
            var sessionFactory = ContextFactory<NodeLocalWebSessionDbContext>.Create(
                sessionPath,
                options => new NodeLocalWebSessionDbContext(options),
                NodeLocalWebSessionDbContext.MigrationsHistoryTableName);
            var grantFactory = ContextFactory<NodeLocalSearchDbContext>.Create(
                grantPath,
                options => new NodeLocalSearchDbContext(options),
                NodeLocalSearchDbContext.MigrationsHistoryTableName);

            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
                identity.Accounts.Add(new InstallationAccountRecord
                {
                    AccountId = "account-admin",
                    NormalizedUsername = "ADMIN",
                    CredentialHash = "digest",
                    CredentialAlgorithm = "test",
                    CredentialCeremonyId = "test",
                    CredentialVersion = 1,
                    Status = InstallationAccountStatus.Active,
                    SecurityVersion = 2,
                    OwnerVersion = 1,
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now,
                });
                await identity.SaveChangesAsync();
                await InstallationAuditTestGenesis.SeedAsync(identity);
            }

            var selectedHandle = "selected-session-handle";
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
                sessions.UserSessions.Add(new WebUserSessionRecord(
                    "session-admin",
                    "account-admin",
                    2,
                    tenantId,
                    "membership-admin",
                    3,
                    "principal-admin",
                    "party-admin",
                    [new PinnedGrantOwnerVersion(
                        "22222222-2222-2222-2222-222222222222",
                        4)],
                    5,
                    AccountSetupInvitationStore.Digest(selectedHandle),
                    "antiforgery-admin",
                    "selection-admin",
                    Now - TimeSpan.FromMinutes(1),
                    Now + TimeSpan.FromMinutes(30),
                    Now + TimeSpan.FromHours(1),
                    1));
                await sessions.SaveChangesAsync();
            }

            await using (var grants = grantFactory.CreateDbContext())
            {
                await grants.Database.EnsureCreatedAsync();
                grants.Grants.Add(new GrantRow
                {
                    GrantId = "22222222-2222-2222-2222-222222222222",
                    TenantId = tenantId,
                    SubjectId = "principal-admin",
                    RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
                    RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
                    ScopeValue = "/",
                    Residency = 0,
                    ValidityFromUnixMs = (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
                    ValidityUntilUnixMs = (Now + TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
                    GrantedBy = "principal-founder",
                    GrantedAtUnixMs = (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
                    GranterKind = (int)GranterKind.Person,
                    Source = (int)GrantSourceKind.Manual,
                    Approver = "principal-founder",
                    LastReviewedAtUnixMs = (Now - TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
                    OwnerVersion = 4,
                });
                grants.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
                {
                    TenantId = tenantId,
                    PrincipalId = "principal-admin",
                    AuthorizationEpoch = 5,
                });
                await grants.SaveChangesAsync();
            }

            var roster = CreateRoster(Guid.Parse(tenantId), inviterPermissions);
            var store = new AccountSetupInvitationStore(identityFactory);
            var issuer = new AccountSetupInvitationIssuer(
                sessionFactory,
                new WebSelectedSessionStore(sessionFactory),
                identityFactory,
                grantFactory,
                new FixedPartyReader("party-admin"),
                new FixedRosterReader(roster),
                store,
                TestAuthorization.AllowGate(),
                new FixedTimeProvider(Now), refusalAudit);
            return new IssueFixture(
                [identityPath, sessionPath, grantPath],
                identityFactory,
                sessionFactory,
                grantFactory,
                tenantId,
                selectedHandle,
                roster,
                issuer);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var path in _paths)
            {
                File.Delete(path);
            }
            return ValueTask.CompletedTask;
        }

        private static MemberRoster CreateRoster(Guid tenantId, PermissionSet inviterPermissions)
        {
            using var founderKey = KeyPair.Generate();
            using var inviterKey = KeyPair.Generate();
            var founderSigner = new Ed25519Signer(founderKey);
            var verifier = new Ed25519Verifier();
            var roster = MemberRoster.Genesis(
                tenantId,
                "party-founder",
                founderSigner,
                verifier,
                Now,
                Guid.Parse("33333333-3333-3333-3333-333333333333"));
            return roster.Admit(
                "party-founder",
                founderSigner,
                "party-admin",
                inviterKey.PrincipalId,
                inviterPermissions,
                verifier,
                Now,
                Guid.Parse("44444444-4444-4444-4444-444444444444"));
        }

        private static string TempPath(string kind) =>
            Path.Combine(Path.GetTempPath(), $"account-setup-{kind}-{Guid.NewGuid():N}.db");
    }

    private sealed class ContextFactory<TContext>(
        string path,
        Func<DbContextOptions<TContext>, TContext> create,
        string historyTable) : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        public static ContextFactory<TContext> Create(
            string path,
            Func<DbContextOptions<TContext>, TContext> create,
            string historyTable) =>
            new(path, create, historyTable);

        public TContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseSqlite($"Data Source={path};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(historyTable))
                .Options;
            return create(options);
        }
    }

    private sealed class FixedRosterReader(MemberRoster roster) : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) =>
            Task.FromResult(roster);
    }

    private sealed class FixedPartyReader(string partyId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(new(
                tenant,
                user,
                new CanonicalPartyReference(partyId)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
