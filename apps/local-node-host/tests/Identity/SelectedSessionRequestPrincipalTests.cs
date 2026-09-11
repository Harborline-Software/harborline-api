using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class SelectedSessionRequestPrincipalTests
{
    // This test exercises selected-principal binding, not ticket-066 audience admission. The exact
    // production status path now carries its explicit device-reachable product-data audience.
    private const string ProbePath = "/api/local-node/status";
    private const string CallerToken = "selected-principal-caller-token";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Two_Live_Sessions_In_One_Tenant_Resolve_Distinct_Parties_On_One_Scoped_Surface()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var first = await fixture.GetAsync(fixture.FirstHandle);
        using var second = await fixture.GetAsync(fixture.SecondHandle);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstSurface = await first.Content.ReadFromJsonAsync<AuthorizationSurface>();
        var secondSurface = await second.Content.ReadFromJsonAsync<AuthorizationSurface>();
        Assert.NotNull(firstSurface);
        Assert.NotNull(secondSurface);
        Assert.Equal(fixture.TenantId, firstSurface!.TenantId);
        Assert.Equal(fixture.TenantId, secondSurface!.TenantId);
        Assert.Equal("party-a", firstSurface.PartyId);
        Assert.Equal("party-b", secondSurface.PartyId);
        Assert.Equal(firstSurface.PartyId, firstSurface.UserId);
        Assert.Equal(secondSurface.PartyId, secondSurface.UserId);
        Assert.NotEqual(firstSurface.PartyId, secondSurface.PartyId);
        Assert.True(firstSurface.SameScopedFacade);
        Assert.True(secondSurface.SameScopedFacade);
        Assert.False(firstSurface.PermissionGranted);
        Assert.False(secondSurface.PermissionGranted);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Stale_Membership_Refuses_Before_Idle_Touch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.ReadSessionAsync(fixture.FirstHandle);
        fixture.Memberships.Replace(fixture.FirstAccountId, fixture.FirstMembership with
        {
            OwnerVersion = fixture.FirstMembership.OwnerVersion + 1,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, ProbePath);
        request.Headers.Add("Cookie", $"__Host-hl-selected={fixture.FirstHandle}");
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await fixture.ReadSessionAsync(fixture.FirstHandle);
        Assert.Equal(before.IdleExpiresAtUtc, after.IdleExpiresAtUtc);
        Assert.Equal(before.OwnerVersion, after.OwnerVersion);
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Two_Pin_Durable_Session_Request_Is_Unauthorized_Not_InternalServerError()
    {
        await using var fixture = await MalformedRecordFixture.CreateAsync();
        await fixture.WriteTwoPinRecordAsync();

        var statusCode = await fixture.InvokeRequestAsync();

        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Two_Pin_Durable_Session_Logout_Refuses_Not_InternalServerError()
    {
        await using var fixture = await MalformedRecordFixture.CreateAsync();
        await fixture.WriteTwoPinRecordAsync();

        var statusCode = await fixture.InvokeLogoutAsync();

        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public void Two_Grant_Pins_Are_Refused_At_Principal_Construction()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new SelectedSessionRequestPrincipal(
                accountId: "account-a",
                tenantId: new TenantId(TenantIdValue),
                principalUserId: new PrincipalUserId("principal-a"),
                canonicalParty: new CanonicalPartyReference("party-a"),
                membershipId: "membership-a",
                membershipOwnerVersion: 3,
                pinnedGrantOwnerVersions:
                [
                    new PinnedGrantOwnerVersion("grant-a", 4),
                    new PinnedGrantOwnerVersion("grant-b", 5),
                ],
                authorizationEpoch: 7,
                sessionCorrelationId: "session-a",
                coordinationCorrelationId: "coordination-a"));

        Assert.Equal(
            "Exactly one live grant pin is required. (Parameter 'pinnedGrantOwnerVersions')",
            exception.Message);
    }

    private sealed record AuthorizationSurface(
        string TenantId,
        string UserId,
        string PartyId,
        string PrincipalId,
        bool SameScopedFacade,
        bool PermissionGranted);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _identityPath;
        private readonly string _sessionPath;
        private readonly ServiceProvider _outerProvider;
        private readonly SharedHostedWebApp _app;
        private readonly WebAccountAccessChallengeIssuerTests.SessionContextFactory _sessionFactory;

        private Fixture(
            string identityPath,
            string sessionPath,
            ServiceProvider outerProvider,
            SharedHostedWebApp app,
            HttpClient client,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            MutableMembershipStore memberships,
            string tenantId,
            string firstHandle,
            string secondHandle,
            string firstAccountId,
            TenantMembershipSnapshot firstMembership)
        {
            _identityPath = identityPath;
            _sessionPath = sessionPath;
            _outerProvider = outerProvider;
            _app = app;
            Client = client;
            _sessionFactory = sessionFactory;
            Memberships = memberships;
            TenantId = tenantId;
            FirstHandle = firstHandle;
            SecondHandle = secondHandle;
            FirstAccountId = firstAccountId;
            FirstMembership = firstMembership;
        }

        internal HttpClient Client { get; }
        internal MutableMembershipStore Memberships { get; }
        internal string TenantId { get; }
        internal string FirstHandle { get; }
        internal string SecondHandle { get; }
        internal string FirstAccountId { get; }
        internal TenantMembershipSnapshot FirstMembership { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var identityPath = Path.Combine(
                Path.GetTempPath(),
                $"selected-principal-identity-{Guid.NewGuid():N}.db");
            var sessionPath = Path.Combine(
                Path.GetTempPath(),
                $"selected-principal-session-{Guid.NewGuid():N}.db");
            var identityFactory =
                new InstallationFounderBootstrapServiceTests.IdentityContextFactory(identityPath);
            var sessionFactory =
                new WebAccountAccessChallengeIssuerTests.SessionContextFactory(sessionPath);
            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
                identity.Accounts.AddRange(
                    Account("account-a", "ALPHA"),
                    Account("account-b", "BRAVO"));
                await identity.SaveChangesAsync();
            }
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
                sessions.UserSessions.AddRange(
                    Session(
                        "handle-a-with-at-least-256-bits-of-test-entropy-000000000000",
                        "session-a",
                        "account-a",
                        "membership-a",
                        "principal-a",
                        "party-a",
                        "grant-a"),
                    Session(
                        "handle-b-with-at-least-256-bits-of-test-entropy-000000000000",
                        "session-b",
                        "account-b",
                        "membership-b",
                        "principal-b",
                        "party-b",
                        "grant-b"));
                await sessions.SaveChangesAsync();
            }

            var tenantId = TenantIdValue;
            var firstMembership = Membership(
                "membership-a", "account-a", "principal-a", "grant-a");
            var secondMembership = Membership(
                "membership-b", "account-b", "principal-b", "grant-b");
            var memberships = new MutableMembershipStore(
                tenantId,
                [firstMembership, secondMembership]);
            var partitionResolver = new FixedPartitionResolver(
                new TenantIdentityAuthorityPartition(
                    tenantId,
                    memberships,
                    new UnusedLeaseCoordinator()));
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory,
                partitionResolver,
                new AcceptingAdmission(),
                new FixedTimeProvider(Now),
                TestAuthorization.Gate(true));
            var selectedStore = new WebSelectedSessionStore(sessionFactory);
            var selectedAuthority = new WebSelectedSessionPrincipalAuthority(
                selectedStore,
                identityFactory,
                coordinator,
                new FixedPartyReader(tenantId),
                Options.Create(new Harborline.Api.Foundation.Session.SessionOptions()),
                new FixedTimeProvider(Now));

            var outer = new ServiceCollection();
            outer.AddTestKernelClock();
            outer.AddLogging();
            outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
            outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
            outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(selectedAuthority);
            var outerProvider = outer.BuildServiceProvider();
            var app = new SharedHostedWebApp(
                outerProvider,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
                outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                outerProvider.GetRequiredService<TimeProvider>());
            app.MapApiRoutes(routes =>
            {
                routes.MapDeviceReachableProductDataGroup().MapGet(
                    ProbePath,
                    (
                        HttpContext http,
                        Harborline.Api.Foundation.Authorization.ITenantContext facade,
                        ICurrentUser currentUser,
                        IAuthorizationContext authorization,
                        Harborline.Api.Foundation.MultiTenancy.ITenantContext tenantContext) =>
                    {
                        var feature = http.Features.Get<SelectedSessionRequestPrincipal>();
                        return feature is null
                            ? Results.Unauthorized()
                            : Results.Json(new AuthorizationSurface(
                                facade.TenantId,
                                currentUser.UserId,
                                feature.CanonicalParty.Value,
                                feature.PrincipalUserId.Value,
                                ReferenceEquals(facade, currentUser) &&
                                ReferenceEquals(currentUser, authorization) &&
                                ReferenceEquals(authorization, tenantContext),
                                authorization.HasPermission("records:read")));
                    });
            });
            await app.StartAsync(CancellationToken.None);
            var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
            return new Fixture(
                identityPath,
                sessionPath,
                outerProvider,
                app,
                client,
                sessionFactory,
                memberships,
                tenantId,
                "handle-a-with-at-least-256-bits-of-test-entropy-000000000000",
                "handle-b-with-at-least-256-bits-of-test-entropy-000000000000",
                "account-a",
                firstMembership);
        }

        internal async Task<HttpResponseMessage> GetAsync(string handle)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProbePath);
            request.Headers.Add("Cookie", $"__Host-hl-selected={handle}");
            return await Client.SendAsync(request);
        }

        internal async Task<WebUserSessionRecord> ReadSessionAsync(string handle)
        {
            await using var sessions = _sessionFactory.CreateDbContext();
            var digest = Digest(handle);
            return await sessions.UserSessions.AsNoTracking()
                .SingleAsync(row => row.HandleDigest == digest);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outerProvider.DisposeAsync();
            File.Delete(_identityPath);
            File.Delete(_sessionPath);
        }
    }

    private sealed class MalformedRecordFixture : IAsyncDisposable
    {
        private const string Handle =
            "malformed-handle-with-at-least-256-bits-of-test-entropy-000000000";

        private readonly string _identityPath;
        private readonly string _sessionPath;
        private readonly WebAccountAccessChallengeIssuerTests.SessionContextFactory _sessionFactory;
        private readonly WebSelectedSessionPrincipalAuthority _principalAuthority;
        private readonly WebSelectedSessionLogoutAuthority _logoutAuthority;

        private MalformedRecordFixture(
            string identityPath,
            string sessionPath,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            WebSelectedSessionPrincipalAuthority principalAuthority,
            WebSelectedSessionLogoutAuthority logoutAuthority)
        {
            _identityPath = identityPath;
            _sessionPath = sessionPath;
            _sessionFactory = sessionFactory;
            _principalAuthority = principalAuthority;
            _logoutAuthority = logoutAuthority;
        }

        internal static async Task<MalformedRecordFixture> CreateAsync()
        {
            var identityPath = Path.Combine(
                Path.GetTempPath(),
                $"selected-malformed-identity-{Guid.NewGuid():N}.db");
            var sessionPath = Path.Combine(
                Path.GetTempPath(),
                $"selected-malformed-session-{Guid.NewGuid():N}.db");
            var identityFactory =
                new InstallationFounderBootstrapServiceTests.IdentityContextFactory(identityPath);
            var sessionFactory =
                new WebAccountAccessChallengeIssuerTests.SessionContextFactory(sessionPath);
            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
                identity.Accounts.Add(Account("account-a", "ALPHA"));
                await identity.SaveChangesAsync();
            }
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
                sessions.UserSessions.Add(Session(
                    Handle,
                    "session-a",
                    "account-a",
                    "membership-a",
                    "principal-a",
                    "party-a",
                    "grant-a"));
                await sessions.SaveChangesAsync();
            }

            var membershipStore = new MutableMembershipStore(
                TenantIdValue,
                [Membership("membership-a", "account-a", "principal-a", "grant-a")]);
            var partitionResolver = new FixedPartitionResolver(
                new TenantIdentityAuthorityPartition(
                    TenantIdValue,
                    membershipStore,
                    new UnusedLeaseCoordinator()));
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory,
                partitionResolver,
                new AcceptingAdmission(),
                new FixedTimeProvider(Now),
                TestAuthorization.Gate(true));
            var store = new WebSelectedSessionStore(sessionFactory);
            var principalAuthority = new WebSelectedSessionPrincipalAuthority(
                store,
                identityFactory,
                coordinator,
                new FixedPartyReader(TenantIdValue),
                Options.Create(new Harborline.Api.Foundation.Session.SessionOptions()),
                new FixedTimeProvider(Now));
            var logoutAuthority = new WebSelectedSessionLogoutAuthority(
                identityFactory,
                store,
                partitionResolver,
                new FixedTimeProvider(Now));
            return new MalformedRecordFixture(
                identityPath,
                sessionPath,
                sessionFactory,
                principalAuthority,
                logoutAuthority);
        }

        internal async Task WriteTwoPinRecordAsync()
        {
            await using var sessions = _sessionFactory.CreateDbContext();
            await sessions.Database.ExecuteSqlAsync($$"""
                UPDATE web_user_sessions
                SET pinned_grant_owner_versions_json =
                    '[{"GrantId":"grant-a","OwnerVersion":4},
                      {"GrantId":"grant-extra","OwnerVersion":5}]'
                WHERE handle_digest = {{Digest(Handle)}}
                """);
        }

        internal async Task<int> InvokeRequestAsync()
        {
            using var requestServices = new ServiceCollection()
                .AddLogging()
                .AddScoped<SelectedSessionTenantContext>()
                .BuildServiceProvider();
            using var scope = requestServices.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Path = ProbePath;
            context.Request.Headers.Cookie = $"__Host-hl-selected={Handle}";
            var app = new ApplicationBuilder(scope.ServiceProvider);
            SharedHostedWebApp.UseListenerCallerAuth(
                app,
                new NodeCallerSessionToken(CallerToken),
                _principalAuthority,
                webAuthority: null,
                scope.ServiceProvider.GetRequiredService<ILogger<SharedHostedWebApp>>());
            app.Run(request =>
            {
                request.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });
            try
            {
                await app.Build()(context);
            }
            catch
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
            return context.Response.StatusCode;
        }

        internal async Task<int> InvokeLogoutAsync()
        {
            using var requestServices = new ServiceCollection()
                .AddLogging()
                .ConfigureHttpJsonOptions(_ => { })
                .BuildServiceProvider();
            using var scope = requestServices.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Headers.Cookie = $"__Host-hl-selected={Handle}";
            await using var body = new MemoryStream();
            context.Response.Body = body;
            try
            {
                var result = await SessionLogoutRoutes.LogoutAsync(
                    _logoutAuthority,
                    NoLegacySessionAuthority.Instance,
                    AcceptingAntiforgeryPolicy.Instance,
                    context);
                await result.ExecuteAsync(context);
            }
            catch
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
            return context.Response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            File.Delete(_identityPath);
            File.Delete(_sessionPath);
        }
    }

    private static readonly string TenantIdValue = Guid.NewGuid().ToString("D");

    private static InstallationAccountRecord Account(string accountId, string username) => new()
    {
        AccountId = accountId,
        NormalizedUsername = username,
        CredentialHash = "fixture-hash",
        CredentialAlgorithm = "argon2id",
        CredentialCeremonyId = $"ceremony-{accountId}",
        CredentialVersion = 1,
        Status = InstallationAccountStatus.Active,
        SecurityVersion = 1,
        OwnerVersion = 1,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private static WebUserSessionRecord Session(
        string handle,
        string correlation,
        string accountId,
        string membershipId,
        string principalId,
        string partyId,
        string grantId) =>
        new(
            SessionCorrelationId: correlation,
            AccountId: accountId,
            AccountSecurityVersion: 1,
            TenantId: TenantIdValue,
            MembershipId: membershipId,
            MembershipOwnerVersion: 3,
            TenantPrincipalId: principalId,
            CanonicalPartyReference: partyId,
            PinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion(grantId, 4)],
            AuthorizationEpoch: 7,
            HandleDigest: Digest(handle),
            AntiforgeryStateId: $"antiforgery-{correlation}",
            CoordinationCorrelationId: $"coordination-{correlation}",
            IssuedAtUtc: Now.AddMinutes(-10),
            IdleExpiresAtUtc: Now.AddMinutes(5),
            AbsoluteExpiresAtUtc: Now.AddHours(1),
            OwnerVersion: 1);

    private static TenantMembershipSnapshot Membership(
        string membershipId,
        string accountId,
        string principalId,
        string grantId) =>
        new(
            membershipId,
            accountId,
            TenantIdValue,
            principalId,
            grantId,
            GrantOwnerVersion: 4,
            AuthorizationEpoch: 7,
            TenantMembershipStatus.Active,
            OwnerVersion: 3);

    private sealed class MutableMembershipStore(
        string tenantId,
        IEnumerable<TenantMembershipSnapshot> memberships)
        : ITenantMembershipAuthorityStore
    {
        private readonly ConcurrentDictionary<string, TenantMembershipSnapshot> _memberships =
            new(memberships.ToDictionary(row => row.AccountId, StringComparer.Ordinal));

        public string TenantId { get; } = tenantId;

        internal void Replace(string accountId, TenantMembershipSnapshot membership) =>
            _memberships[accountId] = membership;

        public Task<TenantMembershipSnapshot?> GetMembershipAsync(
            string accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                _memberships.TryGetValue(accountId, out var membership) ? membership : null);

        public Task<bool> IsAdmissionBlockedAsync(
            string accountId,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task PrepareAsync(string correlationId, string commandFingerprint, string accountId,
            string actorAccountId, string authorityEvidenceDigest, TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantMembershipFinalizationReceipt> FinalizeAsync(string correlationId,
            string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortAsync(string correlationId, string commandFingerprint,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantMembershipIntentState?> GetIntentStateAsync(
            string correlationId,
            CancellationToken cancellationToken) => Task.FromResult<TenantMembershipIntentState?>(null);
        public Task PrepareSessionSelectionAsync(string correlationId, string commandFingerprint,
            string accountId, string membershipId, string payloadDigest,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(string correlationId,
            string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortSessionSelectionAsync(string correlationId, string commandFingerprint,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task PrepareSessionRevocationAsync(string correlationId, string commandFingerprint,
            string accountId, string membershipId, string sessionCorrelationId, string payloadDigest,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(string correlationId,
            string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortSessionRevocationAsync(string correlationId, string commandFingerprint,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedPartitionResolver(TenantIdentityAuthorityPartition partition)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(partition.TenantId, tenantId);
            return Task.FromResult(partition);
        }
    }

    private sealed class FixedPartyReader(string tenantId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default)
        {
            if (tenant.Value != tenantId)
            {
                return ValueTask.FromResult<CanonicalPartyBinding?>(null);
            }
            var party = user.Value switch
            {
                "principal-a" => "party-a",
                "principal-b" => "party-b",
                _ => null,
            };
            return ValueTask.FromResult<CanonicalPartyBinding?>(party is null
                ? null
                : new CanonicalPartyBinding(
                    tenant,
                    user,
                    new CanonicalPartyReference(party)));
        }
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(string actorAccountId, string authorityEvidenceDigest,
            string accountId, TenantMembershipMutation mutation,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<long> ValidateExistingAsync(string accountId, TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.FromResult(membership.AuthorizationEpoch);
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class NoLegacySessionAuthority : INodeWebSessionAuthority
    {
        internal static readonly NoLegacySessionAuthority Instance = new();

        public bool IsEnabled => true;

        public Task<bool> LogoutAsync(HttpContext context, CancellationToken ct) =>
            Task.FromResult(false);

        public void ClearSessionCookie(HttpContext context) =>
            throw new InvalidOperationException("A selected-only logout cannot clear a legacy cookie.");

        public Task<bool> TryAuthenticateAsync(HttpContext context) => throw new NotSupportedException();

        public Task<WebLoginAttemptResult> LoginAsync(
            string? username,
            string? password,
            CancellationToken ct) => throw new NotSupportedException();

        public void IssueSessionCookie(HttpContext context, WebLoginResult login) =>
            throw new NotSupportedException();

        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class AcceptingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        internal static readonly AcceptingAntiforgeryPolicy Instance = new();

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);

        public Task<bool> IssueAnonymousAsync(HttpContext context) => throw new NotSupportedException();
        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => throw new NotSupportedException();
        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            throw new NotSupportedException();
        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            throw new NotSupportedException();
        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            throw new NotSupportedException();
        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) =>
            throw new NotSupportedException();
        public void EmitToken(HttpResponse response, string token) => throw new NotSupportedException();
        public void ExpireAnonymousBinding(HttpResponse response) => throw new NotSupportedException();
    }

    private sealed class UnusedLeaseCoordinator : ILeaseCoordinator
    {
        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ReleaseAsync(Lease lease, CancellationToken ct) =>
            throw new NotSupportedException();
        public bool Holds(string resourceId) => false;
        public IReadOnlyCollection<Lease> HeldLeases => Array.Empty<Lease>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
