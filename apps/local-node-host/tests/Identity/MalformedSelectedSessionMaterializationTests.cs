using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class MalformedSelectedSessionMaterializationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Account_Setup_Issuer_Treats_Two_Pin_Durable_Session_As_Refusal()
    {
        await using var fixture = await Fixture.CreateAsync();

        var status = await RefusalStatusAsync(() => fixture.AccountSetupIssuer.IssueAsync(
            Fixture.SelectedHandle,
            new AccountSetupInvitationIssueRequest(
                Fixture.TenantId,
                ["records:read"],
                "account-setup-two-pin")));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Empty(await identity.AccountSetupInvitations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Admin_Team_Authority_Treats_Two_Pin_Durable_Session_As_Refusal()
    {
        await using var fixture = await Fixture.CreateAsync();

        var status = await RefusalStatusAsync(() => fixture.AdminAuthority.ListMembersAsync(
            Fixture.SelectedHandle,
            Fixture.TenantId));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Recovery_Issuer_Treats_Two_Pin_Durable_Session_As_Refusal()
    {
        await using var fixture = await Fixture.CreateAsync();

        var status = await RefusalStatusAsync(() => fixture.RecoveryIssuer.IssueAsync(
            Fixture.SelectedHandle,
            new RecoveryInvitationIssueRequest(
                Fixture.TenantId,
                "TARGET",
                "recovery-two-pin")));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Empty(await identity.RecoveryInvitations.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Antiforgery_Treats_Two_Pin_Durable_Selected_Session_As_Refusal()
    {
        await using var fixture = await Fixture.CreateAsync();

        var status = await RefusalStatusAsync(() => fixture.Antiforgery.ConsumeSelectedAsync(
            new DefaultHttpContext(),
            Fixture.SelectedHandle));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Empty(await sessions.AntiforgeryStates.AsNoTracking().ToArrayAsync());
    }

    private static async Task<HttpStatusCode> RefusalStatusAsync<T>(Func<Task<T?>> action)
    {
        try
        {
            return await action().ConfigureAwait(false) is null
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.OK;
        }
        catch
        {
            return HttpStatusCode.InternalServerError;
        }
    }

    private static async Task<HttpStatusCode> RefusalStatusAsync(Func<Task<bool>> action)
    {
        try
        {
            return await action().ConfigureAwait(false)
                ? HttpStatusCode.OK
                : HttpStatusCode.Unauthorized;
        }
        catch
        {
            return HttpStatusCode.InternalServerError;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string TenantId = "11111111-1111-1111-1111-111111111111";
        internal const string SelectedHandle =
            "malformed-selected-handle-with-at-least-256-bits-of-fixture-entropy";

        private readonly string[] _paths;

        private Fixture(
            string[] paths,
            WebAccountAccessChallengeIssuerTests.IdentityContextFactory identityFactory,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            AccountSetupInvitationIssuer accountSetupIssuer,
            IAdminTeamAccessAuthority adminAuthority,
            RecoveryInvitationIssuer recoveryIssuer,
            WebAntiforgeryPolicy antiforgery)
        {
            _paths = paths;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            AccountSetupIssuer = accountSetupIssuer;
            AdminAuthority = adminAuthority;
            RecoveryIssuer = recoveryIssuer;
            Antiforgery = antiforgery;
        }

        internal WebAccountAccessChallengeIssuerTests.IdentityContextFactory IdentityFactory { get; }
        internal WebAccountAccessChallengeIssuerTests.SessionContextFactory SessionFactory { get; }
        internal AccountSetupInvitationIssuer AccountSetupIssuer { get; }
        internal IAdminTeamAccessAuthority AdminAuthority { get; }
        internal RecoveryInvitationIssuer RecoveryIssuer { get; }
        internal WebAntiforgeryPolicy Antiforgery { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var identityPath = TempPath("identity");
            var sessionPath = TempPath("session");
            var searchPath = TempPath("search");
            var identityFactory =
                new WebAccountAccessChallengeIssuerTests.IdentityContextFactory(identityPath);
            var sessionFactory =
                new WebAccountAccessChallengeIssuerTests.SessionContextFactory(sessionPath);
            var searchFactory = new SearchContextFactory(searchPath);

            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
                sessions.UserSessions.Add(new WebUserSessionRecord(
                    SessionCorrelationId: "session-two-pin",
                    AccountId: "account-admin",
                    AccountSecurityVersion: 2,
                    TenantId,
                    MembershipId: "membership-admin",
                    MembershipOwnerVersion: 3,
                    TenantPrincipalId: "principal-admin",
                    CanonicalPartyReference: "party-admin",
                    PinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-admin", 4)],
                    AuthorizationEpoch: 5,
                    HandleDigest: Digest(SelectedHandle),
                    AntiforgeryStateId: "antiforgery-admin",
                    CoordinationCorrelationId: "selection-admin",
                    IssuedAtUtc: Now - TimeSpan.FromMinutes(1),
                    IdleExpiresAtUtc: Now + TimeSpan.FromMinutes(30),
                    AbsoluteExpiresAtUtc: Now + TimeSpan.FromHours(1),
                    OwnerVersion: 1));
                await sessions.SaveChangesAsync();
                await sessions.Database.ExecuteSqlAsync($$"""
                    UPDATE web_user_sessions
                    SET pinned_grant_owner_versions_json =
                        '[{"GrantId":"grant-admin","OwnerVersion":4},
                          {"GrantId":"grant-extra","OwnerVersion":5}]'
                    WHERE handle_digest = {{Digest(SelectedHandle)}}
                    """);
            }

            var clock = new FixedTimeProvider();
            var partyReader = new ThrowingPartyReader();
            var rosterReader = new ThrowingRosterReader();
            var selectedSessionStore = new WebSelectedSessionStore(sessionFactory);
            var accountSetupStore = new AccountSetupInvitationStore(identityFactory);
            var accountSetupIssuer = new AccountSetupInvitationIssuer(
                sessionFactory,
                selectedSessionStore,
                identityFactory,
                searchFactory,
                partyReader,
                rosterReader,
                accountSetupStore,
                TestAuthorization.AllowGate(),
                clock);
            var adminGrantStore = TestInMemoryAuthorizationStores.GrantStore();
            var adminAuthority = new AdminTeamAccessAuthority(
                sessionFactory,
                selectedSessionStore,
                identityFactory,
                searchFactory,
                partyReader,
                rosterReader,
                accountSetupStore,
                accountSetupIssuer,
                adminGrantStore,
                new AuthorizedGrantRevocationWriter(adminGrantStore),
                new FixedAuthorizationClosure(),
                TestAuthorization.AllowGate(),
                clock,
                new NoopRosterMemberRevocationAuthority(),
                new Harborline.Api.Kernel.Audit.InMemoryAuditTrail(),
                new Harborline.Api.Foundation.Crypto.Ed25519Signer(Harborline.Api.Foundation.Crypto.KeyPair.Generate()));
            var recoveryIssuer = new RecoveryInvitationIssuer(
                sessionFactory,
                selectedSessionStore,
                identityFactory,
                searchFactory,
                partyReader,
                rosterReader,
                new RecoveryInvitationStore(identityFactory),
                TestAuthorization.AllowGate());
            var antiforgery = new WebAntiforgeryPolicy(
                sessionFactory,
                selectedSessionStore,
                new WebAntiforgeryStateStore(sessionFactory, clock),
                clock);
            return new Fixture(
                [identityPath, sessionPath, searchPath],
                identityFactory,
                sessionFactory,
                accountSetupIssuer,
                adminAuthority,
                recoveryIssuer,
                antiforgery);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var path in _paths)
            {
                File.Delete(path);
            }
            return ValueTask.CompletedTask;
        }

        private static string TempPath(string kind) =>
            Path.Combine(Path.GetTempPath(), $"selected-materialization-{kind}-{Guid.NewGuid():N}.db");
    }

    private sealed class SearchContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalSearchDbContext>
    {
        public NodeLocalSearchDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalSearchDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            return new NodeLocalSearchDbContext(options);
        }
    }

    private sealed class ThrowingPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Malformed session must refuse before party resolution.");
    }

    private sealed class ThrowingRosterReader : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct) =>
            throw new InvalidOperationException("Malformed session must refuse before roster resolution.");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
