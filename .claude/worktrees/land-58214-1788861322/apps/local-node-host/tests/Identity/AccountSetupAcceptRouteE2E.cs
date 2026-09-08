using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// earlier repository ticket #3338 — the invited member's redemption path, driven the way a BROWSER drives it: over a
/// real in-process listener, through the real allowlist, with the real antiforgery handshake, against
/// the real acceptance saga and the real credential store.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tooth.</b> <see cref="Redeeming_An_Invitation_Lets_The_Joiner_Sign_In_With_The_Password_They_Chose"/>
/// does not stop at "acceptance returned 200". It then presents the SAME username and password to the
/// REAL <see cref="WebAccountAccessChallengeIssuer"/> through <c>/api/session/account-challenge</c> and
/// requires a challenge to be issued. That second hop is the whole point: acceptance returning 200 is
/// exactly what a client-computed credential artifact would ALSO produce, while leaving an account
/// whose password can never verify — a redemption that reports success and locks the human out
/// forever. Only the sign-in hop can tell those two apart.
/// </para>
/// <para>
/// <b>Falsification.</b> Make <c>AccountSetupAcceptRoutes</c> derive the credential from anything
/// other than the submitted password — a constant, a re-encoded client value, a second hash of the
/// hash — and the acceptance assert still passes while the sign-in assert goes red. Remove the
/// antiforgery header from <see cref="RedeemAsync"/> and the first hop 400s. Nothing here can be
/// satisfied by a canned response: every status is produced by the node's own handler.
/// </para>
/// <para>
/// <b>Real vs substituted.</b> REAL: the <see cref="SharedHostedWebApp"/> listener and its caller-auth
/// middleware, <see cref="AntiforgeryRoutes"/> + <see cref="WebAntiforgeryPolicy"/> +
/// <see cref="WebAntiforgeryStateStore"/> over a migrated web-session database, the invitation store's
/// own issue + single-use consume, <see cref="AccountSetupAcceptanceService"/>,
/// <see cref="WebJoinerAccountMinter"/> and the installation identity database it writes,
/// <see cref="InitialGrantIssuanceService"/> over a real grant store, the R3-H coordinator, the real
/// Argon2id hasher, and the real challenge issuer that verifies the credential. SUBSTITUTED (the
/// #2614-blessed recipe, none of them this card's tooth): the granter authority, the D2 Party binding
/// minter, the tenant membership store, and the membership admission.
/// </para>
/// <para>
/// <b>Cookies are echoed by hand.</b> The session cookies are <c>__Host-</c> prefixed and
/// <c>Secure</c>, so a cookie jar would refuse to return them over this http loopback listener — the
/// product serves this flow from the node's https origin. Echoing <c>Set-Cookie</c> onto the next
/// request exercises the server's real issue/consume logic without pretending the transport is
/// something it is not.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3338")]
public sealed class AccountSetupAcceptRouteE2E
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 25, 0, TimeSpan.Zero);

    private const string JoinerUsername = "joiner";
    private const string JoinerPassword = "correct horse battery staple joiner";

    [Fact(DisplayName = "A redeemed invitation leaves the joiner able to sign in with the password they chose")]
    public async Task Redeeming_An_Invitation_Lets_The_Joiner_Sign_In_With_The_Password_They_Chose()
    {
        await using var h = await Harness.CreateAsync();

        var redeem = await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword);
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        var accepted = await redeem.Content.ReadFromJsonAsync<AcceptedBody>();
        Assert.Equal(h.TenantId, accepted!.TenantId);

        // The account the saga actually wrote — read from the real installation identity database.
        await using (var identity = h.IdentityFactory.CreateDbContext())
        {
            var account = await identity.Accounts.AsNoTracking()
                .SingleAsync(a => a.NormalizedUsername == "JOINER");
            Assert.Equal(InstallationAccountStatus.Active, account.Status);
            Assert.Equal(Argon2idCredentialArtifact.AlgorithmId, account.CredentialAlgorithm);
        }

        // THE TOOTH. The joiner presents the password they typed to the real challenge issuer. A
        // credential the node did not derive from THIS password refuses here while everything above
        // still passes.
        var signIn = await h.SignInAsync(JoinerUsername, JoinerPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact(DisplayName = "Sign-in after redemption still refuses a password the joiner did not choose")]
    public async Task Sign_In_Refuses_A_Different_Password()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(
            HttpStatusCode.OK,
            (await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword)).StatusCode);

        var wrong = await h.SignInAsync(JoinerUsername, JoinerPassword + " not");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact(DisplayName = "Account challenge sign-in shares one budget across username variants")]
    public async Task Account_Challenge_Rate_Limit_Normalizes_Username_Variants()
    {
        await using var h = await Harness.CreateAsync(new NodeWebLoginLockoutOptions
        {
            MaxFailures = 1,
            Window = TimeSpan.FromMinutes(15),
            BaseLockoutDuration = TimeSpan.FromMinutes(1),
            MaxLockoutDuration = TimeSpan.FromHours(1),
        });

        Assert.Equal(
            HttpStatusCode.OK,
            (await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword)).StatusCode);

        var firstFailure = await h.SignInAsync(
            $"  {JoinerUsername.ToUpperInvariant()}  ",
            JoinerPassword + " not");
        Assert.Equal(HttpStatusCode.Unauthorized, firstFailure.StatusCode);

        // The next request uses a different case and no padding, but the real challenge route must
        // refuse before the real Argon2id-backed issuer runs because both spellings are one account.
        var locked = await h.SignInAsync(JoinerUsername.ToLowerInvariant(), JoinerPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(
            await firstFailure.Content.ReadAsStringAsync(),
            await locked.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "A password the node will not hash is refused without spending the invitation")]
    public async Task Refused_Credential_Leaves_The_Invitation_Redeemable()
    {
        await using var h = await Harness.CreateAsync();

        var refused = await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, password: string.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = await refused.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("credential_rejected", error!.Error);

        // The invitation is single-use and irreversible, so this is the property that matters: an
        // input the human can simply correct must not have cost them their one code.
        var retry = await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(JoinerUsername, JoinerPassword)).StatusCode);
    }

    [Fact(DisplayName = "A taken username leaves the invitation pending so a free-name retry succeeds")]
    public async Task Taken_Username_Leaves_The_Invitation_Pending_And_A_Free_Name_Retry_Succeeds()
    {
        await using var h = await Harness.CreateAsync();

        var conflict = await h.RedeemAsync(h.RawCode, h.TenantId, "founder", JoinerPassword);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("username_taken", (await conflict.Content.ReadFromJsonAsync<ErrorBody>())!.Error);

        // The status code is 409 before and after the fix. Only the durable token state proves the
        // ordinary input error did not spend the invitation.
        var afterConflict = await h.ReadInvitationAsync();
        Assert.Null(afterConflict.ConsumedAtUtc);
        Assert.Equal(2, afterConflict.OwnerVersion);

        var retry = await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.NotNull((await h.ReadInvitationAsync()).ConsumedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(JoinerUsername, JoinerPassword)).StatusCode);
    }

    [Fact(DisplayName = "Three username-conflict disclosures durably consume the invitation")]
    public async Task Username_Conflict_Disclosure_Bound_Consumes_The_Invitation()
    {
        await using var h = await Harness.CreateAsync();

        // Three is intentionally small: enough for two ordinary corrections plus one final choice,
        // while placing a hard ceiling on the names one administrator-issued invitation can probe.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var conflict = await h.RedeemAsync(h.RawCode, h.TenantId, "founder", JoinerPassword);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("username_taken", (await conflict.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
        }

        var bounded = await h.ReadInvitationAsync();
        Assert.NotNull(bounded.ConsumedAtUtc);
        Assert.Equal(4, bounded.OwnerVersion);

        var afterBound = await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, afterBound.StatusCode);
        Assert.Equal("acceptance_failed", (await afterBound.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact(DisplayName = "Redemption without the antiforgery handshake is refused and spends nothing")]
    public async Task Missing_Antiforgery_Is_Refused_And_Leaves_The_Invitation_Redeemable()
    {
        await using var h = await Harness.CreateAsync();

        var bare = await h.PostAsync(
            AccountSetupAcceptRoutes.AcceptPath,
            new { code = h.RawCode, tenantId = h.TenantId, username = JoinerUsername, password = JoinerPassword },
            antiforgeryToken: null,
            cookie: null);
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal("antiforgery_failed", (await bare.Content.ReadFromJsonAsync<ErrorBody>())!.Error);

        Assert.Equal(
            HttpStatusCode.OK,
            (await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword)).StatusCode);
    }

    [Fact(DisplayName = "A second redemption of the same code is refused non-enumerating")]
    public async Task Replayed_Code_Is_Refused()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(
            HttpStatusCode.OK,
            (await h.RedeemAsync(h.RawCode, h.TenantId, JoinerUsername, JoinerPassword)).StatusCode);

        var replay = await h.RedeemAsync(h.RawCode, h.TenantId, "someone-else", JoinerPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("acceptance_failed", (await replay.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact(DisplayName = "The request record never prints the chosen password or the invitation code")]
    public void AcceptRequest_Redacts_Its_Secret_Members()
    {
        var request = new AccountSetupAcceptRoutes.AcceptRequest(
            Code: "invitation-code-that-is-bearer-authority",
            TenantId: "11111111-2222-3333-4444-555555555555",
            Username: JoinerUsername,
            Password: JoinerPassword);

        var printed = request.ToString();

        // A positional record prints every property by default, so one `{Request}` in a log call or
        // an exception message would carry a live credential and an unspent single-use code.
        Assert.DoesNotContain(JoinerPassword, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("invitation-code-that-is-bearer-authority", printed, StringComparison.Ordinal);

        // The non-secret members still print — this is a redaction, not a silenced ToString. Without
        // this half, a PrintMembers that emitted nothing at all would pass the two asserts above.
        Assert.Contains("11111111-2222-3333-4444-555555555555", printed, StringComparison.Ordinal);
        Assert.Contains(JoinerUsername, printed, StringComparison.Ordinal);
    }

    private sealed record AcceptedBody(string TenantId);

    private sealed record ErrorBody(string Error, string Message);

    private sealed class Harness : IAsyncDisposable
    {
        private const string AntiforgeryHeader = "X-Harborline-Antiforgery";

        private readonly string _directory;
        private readonly SearchTestStore _searchStore;
        private readonly ServiceProvider _minterProvider;
        private readonly ServiceProvider _outerProvider;
        private readonly SharedHostedWebApp _app;
        private readonly HttpClient _client;

        private Harness(
            string directory,
            IdentityContextFactory identityFactory,
            SearchTestStore searchStore,
            ServiceProvider minterProvider,
            ServiceProvider outerProvider,
            SharedHostedWebApp app,
            HttpClient client,
            string rawCode,
            string tenantId)
        {
            _directory = directory;
            IdentityFactory = identityFactory;
            _searchStore = searchStore;
            _minterProvider = minterProvider;
            _outerProvider = outerProvider;
            _app = app;
            _client = client;
            RawCode = rawCode;
            TenantId = tenantId;
        }

        public IdentityContextFactory IdentityFactory { get; }

        public string RawCode { get; }

        public string TenantId { get; }

        public static async Task<Harness> CreateAsync(NodeWebLoginLockoutOptions? lockout = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"accept-route-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var time = new FixedTimeProvider(Now);

            var identityFactory = new IdentityContextFactory(Path.Combine(directory, "identity.db"));
            var sessionFactory = new SessionContextFactory(Path.Combine(directory, "session.db"));
            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
            }

            var hasher = new Argon2idPasswordHasher<InstallationAccountRecord>(
                Options.Create(new Argon2idHashOptions()));

            // The founder exists before anyone is invited (the bootstrap ceremony's own product), so
            // the invitation's inviter pins point at a real account.
            var bootstrap = new InstallationFounderBootstrapService(identityFactory, time);
            await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                hasher.HashPassword(HashSubject, "correct horse battery staple founder"),
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap-3338"));

            var tenantId = Guid.NewGuid().ToString("D");
            var invitationStore = new AccountSetupInvitationStore(identityFactory);
            var issued = await invitationStore.IssueAsync(new AccountSetupInvitationSeed(
                TenantId: tenantId,
                InviterAccountId: "inviter-account-3338",
                InviterPrincipalId: "inviter-principal-3338",
                InviterPartyId: "inviter-party-3338",
                InviterSessionCorrelationId: Guid.NewGuid().ToString("N"),
                InviterMembershipId: "inviter-membership-3338",
                InviterMembershipOwnerVersion: 1,
                InviterGrantId: "inviter-grant-3338",
                InviterGrantOwnerVersion: 1,
                InviterAuthorizationEpoch: 1,
                RequestedPermissionsJson: "[\"records:read\"]",
                CommandFingerprint: Digest("command-fingerprint-3338"),
                IssuedAtUtc: Now.AddMinutes(-1),
                AbsoluteExpiresAtUtc: Now.AddHours(24)));
            Assert.NotNull(issued);

            var searchStore = await SearchTestStore.CreateAsync();
            var grantIssuance = new InitialGrantIssuanceService(
                new NodeEfGrantStore(searchStore.Factory), TestAuthorization.AllowGate(), time);
            var resolver = new FixedPartitionResolver(new TenantIdentityAuthorityPartition(
                tenantId, new RecordingMembershipStore(tenantId), new AlwaysLeaseCoordinator()));
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory, resolver, new AcceptingAdmission(), time, TestAuthorization.Gate(true));

            var minterServices = new ServiceCollection();
            minterServices.AddSingleton<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(identityFactory);
            minterServices.AddFrozenKernelClock(time);
            minterServices.AddTransient<WebJoinerAccountMinter>();
            var minterProvider = minterServices.BuildServiceProvider();

            var acceptance = new AccountSetupAcceptanceService(
                invitationStore,
                new FixedAuthorizationClosure(),
                new RecordingPartyBindingMinter(),
                grantIssuance,
                coordinator,
                minterProvider.GetRequiredService<IServiceScopeFactory>(),
                time);

            var antiforgery = new WebAntiforgeryPolicy(
                sessionFactory,
                new WebSelectedSessionStore(sessionFactory),
                new WebAntiforgeryStateStore(sessionFactory, time),
                time);
            var challengeIssuer = new WebAccountAccessChallengeIssuer(
                identityFactory, sessionFactory, hasher, FixtureV1AuthorityGate.Admitting, time);
            var credentials = new WebChosenCredentialFactory(hasher);

            var outer = new ServiceCollection();
            outer.AddFrozenKernelClock(time);
            outer.AddLogging();
            outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
            outer.AddSingleton(new NodeCallerSessionToken("test-per-boot-session-token-3338"));
            var outerProvider = outer.BuildServiceProvider();
            var loginRateLimiter = new WebLoginRateLimiter(
                Options.Create(new NodeWebClientOptions
                {
                    Lockout = lockout ?? new NodeWebLoginLockoutOptions(),
                }),
                time,
                outerProvider.GetRequiredService<ILogger<WebLoginRateLimiter>>());

            var app = new SharedHostedWebApp(
                outerProvider,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new LocalNodeExecutableEndpointRegistry(),
                outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                outerProvider.GetRequiredService<TimeProvider>());

            // Exactly the three routes a redeeming browser touches, mapped the way
            // HostedWebSessionApiEndpoint maps them (closed over the outer authorities, bug-2849).
            app.MapApiRoutes(routes =>
            {
                var preAuth = routes.MapPreAuthOperationalGroup();
                AntiforgeryRoutes.Map(preAuth, antiforgery);
                AccountSetupAcceptRoutes.Map(
                    preAuth,
                    acceptance,
                    credentials,
                    antiforgery,
                    new PairingRedeemRateLimiter(clock: TimeProvider.System));
                AccountChallengeRoutes.Map(
                    preAuth,
                    challengeIssuer,
                    antiforgery,
                    loginRateLimiter);
            });
            await app.StartAsync(CancellationToken.None);

            var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
            return new Harness(
                directory, identityFactory, searchStore, minterProvider, outerProvider, app, client,
                issued!.RawCode, tenantId);
        }

        public async Task<AccountSetupInvitationRecord> ReadInvitationAsync()
        {
            await using var identity = IdentityFactory.CreateDbContext();
            return await identity.AccountSetupInvitations.AsNoTracking().SingleAsync();
        }

        /// <summary>The browser's two-step: fetch anonymous antiforgery state, then POST with it.</summary>
        public async Task<HttpResponseMessage> RedeemAsync(
            string code,
            string tenantId,
            string username,
            string password)
        {
            var (token, cookie) = await IssueAntiforgeryAsync();
            return await PostAsync(
                AccountSetupAcceptRoutes.AcceptPath,
                new { code, tenantId, username, password },
                token,
                cookie);
        }

        /// <summary>The joiner signing in afterwards, through the same anonymous antiforgery handshake.</summary>
        public async Task<HttpResponseMessage> SignInAsync(string username, string password)
        {
            var (token, cookie) = await IssueAntiforgeryAsync();
            return await PostAsync(
                AccountChallengeRoutes.IssuePath,
                new { username, password },
                token,
                cookie);
        }

        public async Task<HttpResponseMessage> PostAsync(
            string path,
            object body,
            string? antiforgeryToken,
            string? cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            if (antiforgeryToken is not null)
            {
                request.Headers.Add(AntiforgeryHeader, antiforgeryToken);
            }
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }
            return await _client.SendAsync(request);
        }

        private async Task<(string Token, string Cookie)> IssueAntiforgeryAsync()
        {
            using var response = await _client.GetAsync(AntiforgeryRoutes.IssuePath);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var token = Assert.Single(response.Headers.GetValues(AntiforgeryHeader));

            // `__Host-` + `Secure` cookies never come back out of a cookie jar over http; echo the
            // name=value pair the node just set. The node's own consume path is untouched by this.
            var setCookie = Assert.Single(
                response.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith(
                    WebSessionCookieNames.AnonymousAntiforgery + "=", StringComparison.Ordinal));
            return (token, setCookie.Split(';', 2)[0]);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outerProvider.DisposeAsync();
            await _minterProvider.DisposeAsync();
            await _searchStore.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static readonly InstallationAccountRecord HashSubject = new()
    {
        AccountId = "fixture",
        NormalizedUsername = "FIXTURE",
        CredentialHash = "not-persisted",
        CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId,
        CredentialCeremonyId = "00000000000000000000000000000000",
        CredentialVersion = 1,
        Status = InstallationAccountStatus.Disabled,
        SecurityVersion = 1,
        OwnerVersion = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class RecordingPartyBindingMinter : IWebJoinerPartyBindingMinter
    {
        public Task<CanonicalPartyReference> MintAsync(
            TenantId tenant, PrincipalUserId joinerPrincipal, PartyId actor,
            string displayName, DateTimeOffset admittedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalPartyReference($"party-{joinerPrincipal.Value[..8]}"));
    }

    private sealed class FixedPartitionResolver(params TenantIdentityAuthorityPartition[] partitions)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult(partitions.Single(p => p.TenantId == tenantId));
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId, string authorityEvidenceDigest, string accountId,
            TenantMembershipMutation mutation, CancellationToken ct) => Task.CompletedTask;

        public Task ValidateExistingAsync(
            string accountId, TenantMembershipSnapshot membership, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingMembershipStore(string tenantId) : ITenantMembershipAuthorityStore
    {
        private TenantMembershipFinalizationReceipt? _receipt;

        public string TenantId { get; } = tenantId;

        public Task PrepareAsync(
            string correlationId, string commandFingerprint, string accountId, string actorAccountId,
            string authorityEvidenceDigest, TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            _receipt ??= new TenantMembershipFinalizationReceipt(
                TenantId,
                DocumentOwnerVersion: 1,
                MembershipId: "membership-3338",
                MembershipOwnerVersion: 1,
                MembershipDigest: new string('a', 64),
                AuditSequence: 1,
                AuditHeadHash: new string('b', 64),
                IntentDigest: new string('c', 64),
                HomeDecisionDigest: new string('d', 64));
            return Task.FromResult(_receipt);
        }

        public Task AbortAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TenantMembershipSnapshot?> GetMembershipAsync(string accountId, CancellationToken ct) =>
            Task.FromResult<TenantMembershipSnapshot?>(null);

        public Task<TenantMembershipIntentState?> GetIntentStateAsync(string correlationId, CancellationToken ct) =>
            Task.FromResult<TenantMembershipIntentState?>(null);

        public Task<bool> IsAdmissionBlockedAsync(string accountId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task PrepareSessionSelectionAsync(
            string correlationId, string commandFingerprint, string accountId, string membershipId,
            string payloadDigest, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AbortSessionSelectionAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task PrepareSessionRevocationAsync(
            string correlationId, string commandFingerprint, string accountId, string membershipId,
            string sessionCorrelationId, string payloadDigest, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AbortSessionRevocationAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);

        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var lease = new Lease(Guid.NewGuid().ToString("N"), resourceId, "test", Now, Now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }

        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }

        public bool Holds(string resourceId) => _held.Values.Any(l => l.ResourceId == resourceId);

        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdentityContextFactory(string databasePath)
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

    private sealed class SessionContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalWebSessionDbContext>
    {
        public NodeLocalWebSessionDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalWebSessionDbContext(options);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
