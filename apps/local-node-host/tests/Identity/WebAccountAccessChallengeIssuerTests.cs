using System.Security.Cryptography;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebAccountAccessChallengeIssuerTests
{
    private static readonly DateTimeOffset FrozenNow =
        new(2026, 7, 17, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Valid_Credential_Issues_One_DigestOnly_VersionPinned_Challenge()
    {
        await using var database = await TestDatabases.CreateAsync();
        const string password = "correct horse battery staple";
        var hasher = CreateHasher();
        var account = await database.SeedActiveAccountAsync(hasher, "FOUNDER", password, securityVersion: 7);
        var issuer = CreateIssuer(database, hasher);

        var result = (await issuer.IssueAsync(" founder ", password)).Challenge;

        Assert.NotNull(result);
        Assert.Equal(FrozenNow + WebAccountAccessChallengeIssuer.ChallengeLifetime, result!.ExpiresAtUtc);
        await using var sessions = database.SessionFactory.CreateDbContext();
        var challenge = await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync();
        Assert.Equal(account.AccountId, challenge.AccountId);
        Assert.Equal(account.SecurityVersion, challenge.AccountSecurityVersion);
        Assert.Equal(FrozenNow, challenge.IssuedAtUtc);
        Assert.Equal(result.ExpiresAtUtc, challenge.AbsoluteExpiresAtUtc);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Handle))),
            challenge.HandleDigest);
        Assert.DoesNotContain(result.Handle, challenge.HandleDigest, StringComparison.Ordinal);
        Assert.Null(challenge.ConsumedAtUtc);
        Assert.Null(challenge.RevokedAtUtc);
        Assert.Equal(1, challenge.OwnerVersion);
        var antiforgery = await sessions.AntiforgeryStates.AsNoTracking().SingleAsync();
        Assert.Equal(WebCookieAudience.AccountChallenge, antiforgery.Audience);
        Assert.Equal(account.AccountId, antiforgery.AccountId);
        Assert.Equal(challenge.ChallengeId, antiforgery.SubjectCorrelationId);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.AntiforgeryToken))),
            antiforgery.TokenDigest);
        Assert.DoesNotContain(result.AntiforgeryToken, antiforgery.TokenDigest, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Unknown_Account_And_Wrong_Password_Create_No_Challenge()
    {
        await using var database = await TestDatabases.CreateAsync();
        const string password = "correct horse battery staple";
        var hasher = CreateHasher();
        await database.SeedActiveAccountAsync(hasher, "FOUNDER", password, securityVersion: 3);
        var issuer = CreateIssuer(database, hasher);

        Assert.Null((await issuer.IssueAsync("FOUNDER", "wrong password")).Challenge);
        Assert.Null((await issuer.IssueAsync("UNKNOWN", password)).Challenge);

        await using var sessions = database.SessionFactory.CreateDbContext();
        Assert.Equal(0, await sessions.AccountAccessChallenges.CountAsync());
        Assert.Equal(0, await sessions.AntiforgeryStates.CountAsync());
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Disabled_Account_With_Correct_Password_Creates_No_Challenge()
    {
        await using var database = await TestDatabases.CreateAsync();
        const string password = "correct horse battery staple";
        var hasher = CreateHasher();
        var account = await database.SeedActiveAccountAsync(
            hasher,
            "FOUNDER",
            password,
            securityVersion: 3);
        await using (var identity = database.IdentityFactory.CreateDbContext())
        {
            var disabled = await identity.Accounts.SingleAsync(row => row.AccountId == account.AccountId);
            disabled.Status = InstallationAccountStatus.Disabled;
            disabled.OwnerVersion++;
            await identity.SaveChangesAsync();
        }
        var issuer = CreateIssuer(database, hasher);

        var result = await issuer.IssueAsync("FOUNDER", password);
        Assert.Null(result.Challenge);
        Assert.True(result.VerifyRan);

        await using var sessions = database.SessionFactory.CreateDbContext();
        Assert.Equal(0, await sessions.AccountAccessChallenges.CountAsync());
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Oversized_Credentials_Are_Rejected_Without_Persisting_A_Challenge()
    {
        await using var database = await TestDatabases.CreateAsync();
        const string password = "correct horse battery staple";
        var hasher = CreateHasher();
        await database.SeedActiveAccountAsync(hasher, "FOUNDER", password, securityVersion: 3);
        var issuer = CreateIssuer(database, hasher);

        var oversizedUsername = await issuer.IssueAsync(new string('U', 321), password);
        var oversizedPassword = await issuer.IssueAsync("FOUNDER", new string('p', 4097));
        Assert.Null(oversizedUsername.Challenge);
        Assert.Equal(WebLoginFailureReason.MalformedInput, oversizedUsername.FailureReason);
        Assert.True(oversizedUsername.VerifyRan);
        Assert.Null(oversizedPassword.Challenge);
        Assert.Equal(WebLoginFailureReason.MalformedInput, oversizedPassword.FailureReason);
        Assert.False(oversizedPassword.VerifyRan);

        await using var sessions = database.SessionFactory.CreateDbContext();
        Assert.Equal(0, await sessions.AccountAccessChallenges.CountAsync());
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Security_Version_Change_During_Verification_Creates_No_Challenge()
    {
        await using var database = await TestDatabases.CreateAsync();
        const string password = "correct horse battery staple";
        var hasher = CreateHasher();
        await database.SeedActiveAccountAsync(hasher, "FOUNDER", password, securityVersion: 3);
        var mutatingHasher = new MutatingPasswordHasher(hasher, () =>
        {
            using var identity = database.IdentityFactory.CreateDbContext();
            var current = identity.Accounts.Single();
            current.SecurityVersion++;
            current.OwnerVersion++;
            identity.SaveChanges();
        });
        var issuer = CreateIssuer(database, mutatingHasher);

        Assert.Null((await issuer.IssueAsync("FOUNDER", password)).Challenge);

        await using var sessions = database.SessionFactory.CreateDbContext();
        Assert.Equal(0, await sessions.AccountAccessChallenges.CountAsync());
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Frozen_Clock_Expires_Challenge_Exactly_At_Ttl_Boundary()
    {
        await using var database = await TestDatabases.CreateAsync();
        const string password = "correct horse battery staple";
        var hasher = CreateHasher();
        await database.SeedActiveAccountAsync(hasher, "FOUNDER", password, securityVersion: 5);
        var issuer = CreateIssuer(database, hasher);

        Assert.NotNull((await issuer.IssueAsync("FOUNDER", password)).Challenge);
        await using var sessions = database.SessionFactory.CreateDbContext();
        var challenge = await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync();

        Assert.False(challenge.IsExpired(challenge.AbsoluteExpiresAtUtc.AddTicks(-1)));
        Assert.True(challenge.IsExpired(challenge.AbsoluteExpiresAtUtc));
        Assert.True(challenge.IsExpired(challenge.AbsoluteExpiresAtUtc.AddTicks(1)));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Route_Returns_Identical_Generic_Failure_Without_Account_Enumeration()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var issuer = new RejectingIssuer();

        var wrong = await InvokeRouteAsync(services, issuer, "FOUNDER", "wrong");
        var unknown = await InvokeRouteAsync(services, issuer, "UNKNOWN", "wrong");

        Assert.Equal(StatusCodes.Status401Unauthorized, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(wrong.Body, unknown.Body);
        Assert.Equal("no-store", wrong.CacheControl);
        Assert.Equal("no-store", unknown.CacheControl);
        Assert.Equal("anonymous-replacement", wrong.Antiforgery);
        Assert.Equal("anonymous-replacement", unknown.Antiforgery);
    }

    [Fact]
    [Trait("PlanCard", "SES-06A")]
    public async Task Route_Refuses_Missing_Or_Replayed_Antiforgery_Before_Credential_Verification()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var antiforgery = new RecordingAntiforgeryPolicy { AcceptAnonymous = false };

        var response = await InvokeRouteAsync(
            services,
            new ThrowingIssuer(),
            "FOUNDER",
            "password",
            antiforgery);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("antiforgery_failed", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Route_Sets_The_Challenge_Cookie_And_Returns_Only_Expiry_Without_Caching()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();

        var response = await InvokeRouteAsync(
            services,
            new SuccessfulIssuer(),
            "FOUNDER",
            "correct horse battery staple");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.DoesNotContain("opaque-challenge", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("challenge", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"expiresAt\":\"2026-07-17T16:05:00+00:00\"", response.Body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Contains("__Host-hl-challenge=opaque-challenge", response.SetCookie, StringComparison.Ordinal);
        Assert.Contains("path=/", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", response.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("challenge-antiforgery", response.Antiforgery);
    }

    [Fact(DisplayName = "An abandoned account challenge releases its pending rate-limit reservation")]
    public async Task Route_Releases_Pending_Reservation_When_Issuer_Throws()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var limiter = new WebLoginRateLimiter(
            Options.Create(new NodeWebClientOptions
            {
                Lockout = new NodeWebLoginLockoutOptions { MaxFailures = 1 },
            }),
            TimeProvider.System,
            NullLogger<WebLoginRateLimiter>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            InvokeRouteAsync(
                services,
                new AbortingIssuer(),
                "FOUNDER",
                "password",
                loginRateLimiter: limiter));

        var retry = await InvokeRouteAsync(
            services,
            new SuccessfulIssuer(),
            "FOUNDER",
            "password",
            loginRateLimiter: limiter);
        Assert.Equal(StatusCodes.Status200OK, retry.StatusCode);
    }

    [Fact(DisplayName = "Typed non-credential refusals do not consume the account login budget")]
    public async Task Route_Does_Not_Count_Provisioning_Refusal()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var issuer = new ProvisioningRefusingIssuer();
        var limiter = new WebLoginRateLimiter(
            Options.Create(new NodeWebClientOptions
            {
                Lockout = new NodeWebLoginLockoutOptions { MaxFailures = 1 },
            }),
            TimeProvider.System,
            NullLogger<WebLoginRateLimiter>.Instance);

        var first = await InvokeRouteAsync(
            services, issuer, "FOUNDER", "password", loginRateLimiter: limiter);
        var second = await InvokeRouteAsync(
            services, issuer, "FOUNDER", "password", loginRateLimiter: limiter);

        Assert.Equal(StatusCodes.Status401Unauthorized, first.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, second.StatusCode);
        Assert.Equal(2, issuer.Calls);
    }

    [Fact(DisplayName = "The challenge route counts malformed usernames after verification")]
    public async Task Route_Counts_Malformed_Username_After_Verification()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var issuer = new MalformedAfterVerificationIssuer();
        var limiter = new WebLoginRateLimiter(
            Options.Create(new NodeWebClientOptions
            {
                Lockout = new NodeWebLoginLockoutOptions { MaxFailures = 1 },
            }),
            TimeProvider.System,
            NullLogger<WebLoginRateLimiter>.Instance);

        for (var i = 0; i < 2; i++)
        {
            var response = await InvokeRouteAsync(
                services, issuer, " ", "x", loginRateLimiter: limiter);
            Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        }

        Assert.Equal(1, issuer.Calls);
    }

    /// <summary>
    /// The challenge's 0/1/N classification (earlier repository ticket #3329 step 2), asserted on the SERVER.
    /// </summary>
    /// <remarks>
    /// Every other route test here resolves through a container with no
    /// <c>IInstallationTenantCandidateLocator</c>, so they all exercise only the null-locator early
    /// return. Without these, nothing verified the server actually emits the shape the client parses,
    /// and a refactor moving classification ABOVE the credential check would be caught by nothing.
    /// </remarks>
    [Theory]
    [InlineData(0, "none")]
    [InlineData(1, "single")]
    [InlineData(3, "multiple")]
    public async Task Route_Classifies_The_Callers_Own_Workspaces(int count, string expected)
    {
        using var services = ServicesWithLocator(new StubLocator(count));

        var response = await InvokeRouteAsync(services, new SuccessfulIssuer(), "FOUNDER", "pw");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(expected, body.RootElement.GetProperty("classification").GetString());

        // Candidates are sent for the MULTIPLE case only — the others need no list, and a narrower
        // response is a narrower disclosure.
        var candidates = body.RootElement.GetProperty("candidates");
        Assert.Equal(count == 3 ? 3 : 0, candidates.GetArrayLength());
        foreach (var candidate in candidates.EnumerateArray())
        {
            Assert.True(candidate.TryGetProperty("tenantId", out _));
            // No label on the wire: the locator's DisplayLabel is the tenant GUID today, and mapping a
            // future tenant-owned label through here would widen this response with no re-review.
            Assert.False(candidate.TryGetProperty("displayName", out _));
        }
    }

    [Fact]
    public async Task Route_Refusal_Carries_No_Classification_At_All()
    {
        // The disclosure invariant: a caller who failed the credential check learns nothing about any
        // account's workspaces, and the locator is never even consulted.
        var locator = new StubLocator(3);
        using var services = ServicesWithLocator(locator);

        var response = await InvokeRouteAsync(services, new RejectingIssuer(), "FOUNDER", "wrong");

        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.DoesNotContain("classification", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("candidates", response.Body, StringComparison.Ordinal);
        Assert.Equal(0, locator.Calls);
    }

    [Fact]
    public async Task Route_Degrades_To_Unknown_When_Classification_Throws()
    {
        // Fail-SOFT is the whole contract: a classification failure must never cost a sign-in that
        // would otherwise work. The client reads "unknown" and falls back to the null-tenant path.
        using var services = ServicesWithLocator(new ThrowingLocator());

        var response = await InvokeRouteAsync(services, new SuccessfulIssuer(), "FOUNDER", "pw");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal("unknown", body.RootElement.GetProperty("classification").GetString());
        // The challenge itself still succeeded — the cookie is set and the session can proceed.
        Assert.Contains("httponly", response.SetCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceProvider ServicesWithLocator(IInstallationTenantCandidateLocator locator) =>
        new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .AddSingleton(locator)
            .BuildServiceProvider();

    private sealed class StubLocator(int count) : IInstallationTenantCandidateLocator
    {
        internal int Calls { get; private set; }

        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            CancellationToken cancellationToken = default) =>
            ListForAccountAsync(accountPrincipal, null, cancellationToken);

        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            string? excludedCorrelationId,
            CancellationToken cancellationToken = default)
        {
            Calls += 1;
            IReadOnlyList<InstallationTenantCandidate> list = Enumerable
                .Range(0, count)
                .Select(i => new InstallationTenantCandidate(
                    new TenantId($"0000000{i}-0000-4000-8000-000000000000"),
                    "label-should-not-reach-the-wire",
                    TenantMembershipStatus.Active))
                .ToArray();
            return Task.FromResult(list);
        }
    }

    private sealed class ThrowingLocator : IInstallationTenantCandidateLocator
    {
        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("induced classification fault");

        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            string? excludedCorrelationId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("induced classification fault");
    }

    private static async Task<(
        int StatusCode,
        string Body,
        string CacheControl,
        string SetCookie,
        string Antiforgery)> InvokeRouteAsync(
        IServiceProvider services,
        IWebAccountAccessChallengeIssuer issuer,
        string username,
        string password,
        RecordingAntiforgeryPolicy? antiforgery = null,
        WebLoginRateLimiter? loginRateLimiter = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        antiforgery ??= new RecordingAntiforgeryPolicy();
        loginRateLimiter ??= new WebLoginRateLimiter(
            Options.Create(new NodeWebClientOptions()),
            TimeProvider.System,
            NullLogger<WebLoginRateLimiter>.Instance);
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var result = await AccountChallengeRoutes.IssueAsync(
            issuer,
            antiforgery,
            loginRateLimiter,
            new AccountChallengeRoutes.IssueRequest(username, password),
            context);
        await result.ExecuteAsync(context);
        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return (
            context.Response.StatusCode,
            await reader.ReadToEndAsync(),
            context.Response.Headers.CacheControl.ToString(),
            context.Response.Headers.SetCookie.ToString(),
            context.Response.Headers[WebAntiforgeryPolicy.HeaderName].ToString());
    }

    private static Argon2idPasswordHasher<InstallationAccountRecord> CreateHasher() =>
        new(Options.Create(new Argon2idHashOptions()));

    /// <summary>
    /// #3245 F2: the REAL issuer stops minting account challenges once the REAL v2 marker commits.
    /// </summary>
    /// <remarks>
    /// Called by <c>LegacyV1CutoverAuthorityProof.ProvePostCutoverV1BearerIsRejected</c> as well as
    /// by xUnit, so the cutover fixture drives the ISSUE half of the account-challenge audience
    /// through the production authority. The founder is bootstrapped through the real ceremony rather
    /// than seeded as a bare row, because the marker CAS re-verifies the initial installation-root
    /// designation that ceremony creates.
    /// </remarks>
    [Fact(DisplayName =
        "#3245 F2: the account-challenge issuer refuses after the REAL v2 marker commits")]
    [Trait("PlanCard", "MTW-01E")]
    public async Task Challenge_Issue_Is_Refused_After_The_Real_V2_Marker_Commits()
    {
        await using var database = await TestDatabases.CreateAsync();
        const string password = "correct horse battery staple";
        var hasher = CreateHasher();
        // The Argon2id hasher derives from the password alone, so this credential record only has to be
        // well-formed; the founder ceremony below is what actually persists the credential.
        var credentialHash = hasher.HashPassword(
            new InstallationAccountRecord
            {
                AccountId = "credential-hash-harborline",
                NormalizedUsername = "FOUNDER",
                CredentialHash = string.Empty,
                CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId,
                CredentialCeremonyId = Guid.NewGuid().ToString("N"),
            },
            password);

        var bootstrap = new InstallationFounderBootstrapService(
            database.IdentityFactory,
            new FixedTimeProvider(FrozenNow));
        var founder = await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
            "founder",
            credentialHash,
            Guid.NewGuid().ToString("N"),
            string.Join(":", Enumerable.Repeat("AB", 32)),
            "mtw-3245-issuer-cutover"));
        Assert.Equal(InstallationFounderBootstrapStatus.Created, founder.Status);

        var cutover = new InstallationIdentityCutoverOrchestrator(
            database.IdentityFactory,
            new FixedTimeProvider(FrozenNow),
            Harborline.Api.LocalNodeHost.Data.Identity.InstallationAuthorityVersionRegistry
                .CreateDefault([new TestSignInPath("test-successor")]));
        var issuer = new WebAccountAccessChallengeIssuer(
            database.IdentityFactory,
            database.SessionFactory,
            hasher,
            cutover,
            new FixedTimeProvider(FrozenNow));

        // The ADMITTING state first: pre-cutover the real credential mints a real challenge. Without
        // this half, an issuer that refused everything would satisfy the assertion below.
        Assert.NotNull((await issuer.IssueAsync("founder", password)).Challenge);

        await CutoverAdvance.CommitV2MarkerAsync(database.IdentityFactory, cutover, FrozenNow);

        // The same credential now mints nothing, and no new challenge row appears.
        Assert.Null((await issuer.IssueAsync("founder", password)).Challenge);
        await using var sessions = database.SessionFactory.CreateDbContext();
        Assert.Equal(1, await sessions.AccountAccessChallenges.CountAsync());
    }

    private static WebAccountAccessChallengeIssuer CreateIssuer(
        TestDatabases database,
        IPasswordHasher<InstallationAccountRecord> hasher) =>
        new(
            database.IdentityFactory,
            database.SessionFactory,
            hasher,
            FixtureV1AuthorityGate.Admitting,
            new FixedTimeProvider(FrozenNow));

    private sealed class RejectingIssuer : IWebAccountAccessChallengeIssuer
    {
        public Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebAccountAccessChallengeAttemptResult(
                null,
                WebLoginFailureReason.CredentialMismatch,
                VerifyRan: false));
    }

    private sealed class SuccessfulIssuer : IWebAccountAccessChallengeIssuer
    {
        public Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebAccountAccessChallengeAttemptResult(
                new("opaque-challenge", "challenge-antiforgery", FrozenNow.AddMinutes(5), "account-1"),
                null,
                VerifyRan: true));
    }

    private sealed class ThrowingIssuer : IWebAccountAccessChallengeIssuer
    {
        public Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The issuer must not run before antiforgery validation.");
    }

    private sealed class AbortingIssuer : IWebAccountAccessChallengeIssuer
    {
        public Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class ProvisioningRefusingIssuer : IWebAccountAccessChallengeIssuer
    {
        public int Calls { get; private set; }

        public Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new WebAccountAccessChallengeAttemptResult(
                null,
                WebLoginFailureReason.ProvisioningRefused,
                VerifyRan: false));
        }
    }

    private sealed class MalformedAfterVerificationIssuer : IWebAccountAccessChallengeIssuer
    {
        public int Calls { get; private set; }

        public Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
            string? username,
            string? password,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new WebAccountAccessChallengeAttemptResult(
                null,
                WebLoginFailureReason.MalformedInput,
                VerifyRan: true));
        }
    }

    private sealed class RecordingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public bool AcceptAnonymous { get; init; } = true;

        public Task<bool> IssueAnonymousAsync(HttpContext context)
        {
            EmitToken(context.Response, "anonymous-replacement");
            return Task.FromResult(true);
        }

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) =>
            Task.FromResult(AcceptAnonymous);

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) =>
            throw new NotSupportedException();

        public void EmitToken(HttpResponse response, string token) =>
            response.Headers[WebAntiforgeryPolicy.HeaderName] = token;

        public void ExpireAnonymousBinding(HttpResponse response) =>
            response.Cookies.Delete(WebSessionCookieNames.AnonymousAntiforgery);
    }

    private sealed class MutatingPasswordHasher(
        IPasswordHasher<InstallationAccountRecord> inner,
        Action mutateAfterSuccessfulVerification) : IPasswordHasher<InstallationAccountRecord>
    {
        public string HashPassword(InstallationAccountRecord user, string password) =>
            inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            InstallationAccountRecord user,
            string hashedPassword,
            string providedPassword)
        {
            var result = inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            if (result != PasswordVerificationResult.Failed)
            {
                mutateAfterSuccessfulVerification();
            }

            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestDatabases : IAsyncDisposable
    {
        private readonly string _identityPath;
        private readonly string _sessionPath;

        private TestDatabases(string identityPath, string sessionPath)
        {
            _identityPath = identityPath;
            _sessionPath = sessionPath;
            IdentityFactory = new IdentityContextFactory(identityPath);
            SessionFactory = new SessionContextFactory(sessionPath);
        }

        public IdentityContextFactory IdentityFactory { get; }

        public SessionContextFactory SessionFactory { get; }

        public static async Task<TestDatabases> CreateAsync()
        {
            var identityPath = Path.Combine(
                Path.GetTempPath(),
                $"account-challenge-identity-{Guid.NewGuid():N}.db");
            var sessionPath = Path.Combine(
                Path.GetTempPath(),
                $"account-challenge-session-{Guid.NewGuid():N}.db");
            var databases = new TestDatabases(identityPath, sessionPath);
            await using (var identity = databases.IdentityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }
            await using (var sessions = databases.SessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
            }

            return databases;
        }

        public async Task<InstallationAccountRecord> SeedActiveAccountAsync(
            IPasswordHasher<InstallationAccountRecord> hasher,
            string normalizedUsername,
            string password,
            long securityVersion)
        {
            var account = new InstallationAccountRecord
            {
                AccountId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                NormalizedUsername = normalizedUsername,
                CredentialHash = string.Empty,
                CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId,
                CredentialCeremonyId = Guid.NewGuid().ToString("N"),
                CredentialVersion = 1,
                Status = InstallationAccountStatus.Active,
                SecurityVersion = securityVersion,
                OwnerVersion = 1,
                CreatedAtUtc = FrozenNow,
                UpdatedAtUtc = FrozenNow,
            };
            account.CredentialHash = hasher.HashPassword(account, password);

            await using var identity = IdentityFactory.CreateDbContext();
            identity.Accounts.Add(account);
            await identity.SaveChangesAsync();
            return account;
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(_identityPath);
            File.Delete(_sessionPath);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class IdentityContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }

    public sealed class SessionContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalWebSessionDbContext>
    {
        public NodeLocalWebSessionDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalWebSessionDbContext(options);
        }
    }

    /// <summary>
    /// Since #3615 the cutover refuses to commit the marker while no v2 sign-in path is registered.
    /// This test drives the REAL marker in order to assert the post-flip refusal, so it declares a
    /// stub successor. The pre-flip refusal is proved separately.
    /// </summary>
    private sealed record TestSignInPath(string SignInPathName)
        : Harborline.Api.LocalNodeHost.Data.Identity.IInstallationAuthorityV2SignInPath;
}
