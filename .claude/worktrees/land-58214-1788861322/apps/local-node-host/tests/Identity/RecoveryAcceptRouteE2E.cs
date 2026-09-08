using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// earlier repository ticket #3366 — account recovery driven the way a BROWSER drives it: over a real in-process
/// listener, through the real allowlist, with the real antiforgery handshake, against the real
/// recovery saga, the real Argon2id hasher, and the real challenge issuer that verifies afterwards.
/// The recovery twin of <see cref="AccountSetupAcceptRouteE2E"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tooth.</b> <see cref="Recovering_Lets_The_Human_Sign_In_With_The_Password_They_Chose"/>
/// does not stop at "recovery returned 200". It then presents the SAME username and the NEW password
/// to the REAL <see cref="WebAccountAccessChallengeIssuer"/> through
/// <c>/api/session/account-challenge</c> and requires a challenge to be issued. That second hop is
/// the whole point: recovery returning 200 is exactly what a browser-computed credential artifact
/// would ALSO produce, while rotating the account onto a credential that can never verify — a
/// recovery that reports success and locks the human out permanently, on the path they reached
/// precisely BECAUSE they were already locked out. Only the sign-in hop can tell those two apart.
/// </para>
/// <para>
/// <b>Falsification.</b> Make <see cref="RecoveryAcceptRoutes"/> derive the credential from anything
/// other than the submitted password — a constant, a re-encoded client value, a second hash of the
/// hash — and the recovery assert (200), the rotation asserts (credential + security version
/// advanced), and the revocation asserts ALL still pass, while the sign-in assert goes red. Note
/// that <see cref="Sign_In_Refuses_The_Old_Password"/> ALSO still passes under that mutation: a
/// negative-only assertion pins the broken outcome just as well as the working one
/// (<c>bug-20260729-cabe04e3</c>), which is why it cannot stand alone.
/// </para>
/// <para>
/// <b>Real vs substituted.</b> REAL: the <see cref="SharedHostedWebApp"/> listener and its caller-auth
/// middleware, <see cref="AntiforgeryRoutes"/> + <see cref="WebAntiforgeryPolicy"/> +
/// <see cref="WebAntiforgeryStateStore"/> over a migrated web-session database, the founder bootstrap
/// ceremony, <see cref="RecoveryInvitationStore"/>'s own issue + single-use consume,
/// <see cref="AccountCredentialRecoveryService"/>, <see cref="RecoverySessionRevoker"/> and the
/// session database it writes revocations to, <see cref="WebChosenCredentialFactory"/> over the real
/// <see cref="Argon2idPasswordHasher{TUser}"/>, and the real challenge issuer that verifies the
/// rotated credential. SUBSTITUTED: nothing this card's tooth depends on — recovery needs no granter,
/// Party, membership or grant machinery.
/// </para>
/// <para>
/// <b>Cookies are echoed by hand.</b> The session cookies are <c>__Host-</c> prefixed and
/// <c>Secure</c>, so a cookie jar would refuse to return them over this http loopback listener — the
/// product serves this flow from the node's https origin. Echoing <c>Set-Cookie</c> onto the next
/// request exercises the server's real issue/consume logic without pretending the transport is
/// something it is not.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3366")]
public sealed class RecoveryAcceptRouteE2E
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 10, 15, 0, TimeSpan.Zero);

    private const string HolderUsername = "founder";
    private const string OldPassword = "correct horse battery staple original";
    private const string NewPassword = "correct horse battery staple recovered";

    [Fact(DisplayName = "A redeemed recovery code lets the human sign in with the password they chose")]
    public async Task Recovering_Lets_The_Human_Sign_In_With_The_Password_They_Chose()
    {
        await using var h = await Harness.CreateAsync();

        // The account is reachable with the OLD password before recovery — so the sign-in hop below
        // is proven to be a working oracle, not a surface that refuses everything.
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(HolderUsername, OldPassword)).StatusCode);

        var recovered = await h.RecoverAsync(h.RawCode, NewPassword);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("recovered", (await recovered.Content.ReadFromJsonAsync<RecoveredBody>())!.Status);

        // THE TOOTH. The human presents the password they typed to the real challenge issuer. A
        // credential the node did not derive from THIS password refuses here while everything else
        // in this test still passes.
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(HolderUsername, NewPassword)).StatusCode);
    }

    [Fact(DisplayName = "Recovery advances the credential and security versions and revokes every prior session")]
    public async Task Recovery_Advances_Versions_And_Revokes_Prior_Sessions()
    {
        await using var h = await Harness.CreateAsync();

        // A live selected session for this account, pinning the pre-recovery security version.
        await h.SeedSelectedSessionAsync("sel-pre-recovery", Digest("handle-pre-recovery"));

        Assert.Equal(HttpStatusCode.OK, (await h.RecoverAsync(h.RawCode, NewPassword)).StatusCode);

        // The DURABLE record, not the status code — a 200 is returned either way. D3's two
        // recovery-specific clauses: the version advances, and prior sessions are revoked before
        // recovery becomes visible. A credential-minting change is exactly where a version increment
        // gets dropped, so both are asserted against the databases the saga wrote.
        await using (var identity = h.IdentityFactory.CreateDbContext())
        {
            var account = await identity.Accounts.AsNoTracking()
                .SingleAsync(a => a.NormalizedUsername == "FOUNDER");
            Assert.Equal(2, account.CredentialVersion);
            Assert.Equal(2, account.SecurityVersion);
            Assert.Equal(Argon2idCredentialArtifact.AlgorithmId, account.CredentialAlgorithm);
            Assert.Equal(h.AccountId, account.AccountId);
        }

        await using (var sessions = h.SessionFactory.CreateDbContext())
        {
            var revocation = await sessions.Revocations.AsNoTracking()
                .SingleAsync(r => r.AccountId == h.AccountId);
            Assert.Equal(WebCookieAudience.SelectedSession, revocation.Audience);
            Assert.Equal("account-credential-recovery", revocation.ReasonCode);
        }

        // And the revoked session is unresolvable at the advanced version — the revocation is a
        // fact the session store enforces, not just a row.
        var selectedStore = new WebSelectedSessionStore(h.SessionFactory);
        Assert.Null(await selectedStore.FindActiveAsync(Digest("handle-pre-recovery"), 2, Now.AddMinutes(1)));
    }

    [Fact(DisplayName = "Sign-in after recovery refuses the password the account had before")]
    public async Task Sign_In_Refuses_The_Old_Password()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(HttpStatusCode.OK, (await h.RecoverAsync(h.RawCode, NewPassword)).StatusCode);

        var stale = await h.SignInAsync(HolderUsername, OldPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact(DisplayName = "A password the node will not hash is refused without spending the recovery code")]
    public async Task Refused_Credential_Leaves_The_Recovery_Code_Redeemable()
    {
        await using var h = await Harness.CreateAsync();

        var refused = await h.RecoverAsync(h.RawCode, password: string.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("credential_rejected", (await refused.Content.ReadFromJsonAsync<ErrorBody>())!.Error);

        // The recovery code is single-use and irreversible, and the human reaching this route has no
        // other way in: an input they can simply correct must not have cost them their one code.
        Assert.Equal(HttpStatusCode.OK, (await h.RecoverAsync(h.RawCode, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.SignInAsync(HolderUsername, NewPassword)).StatusCode);
    }

    [Fact(DisplayName = "Recovery without the antiforgery handshake is refused and spends nothing")]
    public async Task Missing_Antiforgery_Is_Refused_And_Leaves_The_Code_Redeemable()
    {
        await using var h = await Harness.CreateAsync();

        var bare = await h.PostAsync(
            RecoveryAcceptRoutes.RecoverPath,
            new { code = h.RawCode, password = NewPassword },
            antiforgeryToken: null,
            cookie: null);
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal("antiforgery_failed", (await bare.Content.ReadFromJsonAsync<ErrorBody>())!.Error);

        Assert.Equal(HttpStatusCode.OK, (await h.RecoverAsync(h.RawCode, NewPassword)).StatusCode);
    }

    [Fact(DisplayName = "A second redemption of the same recovery code is refused non-enumerating")]
    public async Task Replayed_Code_Is_Refused()
    {
        await using var h = await Harness.CreateAsync();

        Assert.Equal(HttpStatusCode.OK, (await h.RecoverAsync(h.RawCode, NewPassword)).StatusCode);

        var replay = await h.RecoverAsync(h.RawCode, NewPassword + " again");
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("recovery_failed", (await replay.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact(DisplayName = "The recovery request record never prints the chosen password or the recovery code")]
    public void RecoverRequest_Redacts_Its_Secret_Members()
    {
        var request = new RecoveryAcceptRoutes.RecoverRequest(
            Code: "recovery-code-that-is-bearer-authority",
            Password: NewPassword);

        var printed = request.ToString();

        // A positional record prints every property by default, so one `{Request}` in a log call or
        // an exception message would carry a live credential and an unspent single-use code.
        Assert.DoesNotContain(NewPassword, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery-code-that-is-bearer-authority", printed, StringComparison.Ordinal);

        // Both member NAMES still print, so this is a redaction rather than a silenced ToString: a
        // PrintMembers that emitted nothing at all would satisfy the two asserts above.
        Assert.Contains("Code = <redacted>", printed, StringComparison.Ordinal);
        Assert.Contains("Password = <redacted>", printed, StringComparison.Ordinal);
    }

    private sealed record RecoveredBody(string Status);

    private sealed record ErrorBody(string Error, string Message);

    private sealed class Harness : IAsyncDisposable
    {
        private const string AntiforgeryHeader = "X-Harborline-Antiforgery";

        private readonly string _directory;
        private readonly ServiceProvider _outerProvider;
        private readonly SharedHostedWebApp _app;
        private readonly HttpClient _client;

        private Harness(
            string directory,
            IdentityContextFactory identityFactory,
            SessionContextFactory sessionFactory,
            ServiceProvider outerProvider,
            SharedHostedWebApp app,
            HttpClient client,
            string rawCode,
            string accountId)
        {
            _directory = directory;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            _outerProvider = outerProvider;
            _app = app;
            _client = client;
            RawCode = rawCode;
            AccountId = accountId;
        }

        public IdentityContextFactory IdentityFactory { get; }

        public SessionContextFactory SessionFactory { get; }

        public string RawCode { get; }

        public string AccountId { get; }

        public static async Task<Harness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"recovery-route-{Guid.NewGuid():N}");
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

            // The locked-out human's account, established by the real bootstrap ceremony with a real
            // Argon2id credential — so "sign in with the OLD password" is a live control.
            var bootstrap = new InstallationFounderBootstrapService(identityFactory, time);
            await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                HolderUsername,
                hasher.HashPassword(HashSubject, OldPassword),
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap-3366"));

            string accountId;
            await using (var identity = identityFactory.CreateDbContext())
            {
                accountId = (await identity.Accounts.AsNoTracking()
                    .SingleAsync(a => a.NormalizedUsername == "FOUNDER")).AccountId;
            }

            var recoveryStore = new RecoveryInvitationStore(identityFactory);
            var issued = await recoveryStore.IssueAsync(new RecoveryInvitationSeed(
                TenantId: Guid.NewGuid().ToString("D"),
                IssuerAccountId: "admin-account-3366",
                IssuerPrincipalId: "admin-principal-3366",
                TargetAccountId: accountId,
                TargetNormalizedUsername: "FOUNDER",
                CommandFingerprint: Digest("recovery-command-3366"),
                IssuedAtUtc: Now.AddMinutes(-1),
                AbsoluteExpiresAtUtc: Now.AddHours(1)));
            Assert.NotNull(issued);

            var recovery = new AccountCredentialRecoveryService(
                recoveryStore, new RecoverySessionRevoker(sessionFactory), identityFactory, time);
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
            outer.AddSingleton(new NodeCallerSessionToken("test-per-boot-session-token-3366"));
            var outerProvider = outer.BuildServiceProvider();
            var loginRateLimiter = new WebLoginRateLimiter(
                Options.Create(new NodeWebClientOptions()),
                time,
                outerProvider.GetRequiredService<ILogger<WebLoginRateLimiter>>());

            var app = new SharedHostedWebApp(
                outerProvider,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new LocalNodeExecutableEndpointRegistry(),
                outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                outerProvider.GetRequiredService<TimeProvider>());

            // Exactly the three routes a recovering browser touches, mapped the way
            // HostedWebSessionApiEndpoint maps them (closed over the outer authorities, bug-2849).
            app.MapApiRoutes(routes =>
            {
                var preAuth = routes.MapPreAuthOperationalGroup();
                AntiforgeryRoutes.Map(preAuth, antiforgery);
                RecoveryAcceptRoutes.Map(
                    preAuth,
                    recovery,
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
                directory, identityFactory, sessionFactory, outerProvider, app, client,
                issued!.RawCode, accountId);
        }

        /// <summary>The browser's two-step: fetch anonymous antiforgery state, then POST with it.</summary>
        public async Task<HttpResponseMessage> RecoverAsync(string code, string password)
        {
            var (token, cookie) = await IssueAntiforgeryAsync();
            return await PostAsync(
                RecoveryAcceptRoutes.RecoverPath,
                new { code, password },
                token,
                cookie);
        }

        /// <summary>The human signing in, through the same anonymous antiforgery handshake.</summary>
        public async Task<HttpResponseMessage> SignInAsync(string username, string password)
        {
            var (token, cookie) = await IssueAntiforgeryAsync();
            return await PostAsync(
                AccountChallengeRoutes.IssuePath,
                new { username, password },
                token,
                cookie);
        }

        /// <summary>Writes a live selected session for the account, pinned at security version 1.</summary>
        public async Task SeedSelectedSessionAsync(string correlationId, string handleDigest)
        {
            await using var sessions = SessionFactory.CreateDbContext();
            sessions.UserSessions.Add(new WebUserSessionRecord(
                SessionCorrelationId: correlationId,
                AccountId: AccountId,
                AccountSecurityVersion: 1,
                TenantId: Guid.NewGuid().ToString("D"),
                MembershipId: "membership-" + correlationId,
                MembershipOwnerVersion: 1,
                TenantPrincipalId: "principal-" + correlationId,
                CanonicalPartyReference: "party-" + correlationId,
                PinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-" + correlationId, 1)],
                AuthorizationEpoch: 1,
                HandleDigest: handleDigest,
                AntiforgeryStateId: "antiforgery-" + correlationId,
                CoordinationCorrelationId: "coordination-" + correlationId,
                IssuedAtUtc: Now.AddMinutes(-5),
                IdleExpiresAtUtc: Now.AddMinutes(10),
                AbsoluteExpiresAtUtc: Now.AddHours(1),
                OwnerVersion: 1));
            await sessions.SaveChangesAsync();
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
