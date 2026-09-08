using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Foundation.PasswordHashing.DependencyInjection;
using Harborline.Api.Foundation.Session;
using Harborline.Api.Foundation.Session.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// End-to-end proof of the WEB-CLIENT per-user session (DOGFOOD.md gap #1): a browser user logs in
/// against the node's roster credential, the node mints an EXPIRING bearer, and the listener
/// caller-auth gate accepts THAT bearer on a gated route (in addition to the bootstrap token) —
/// fail-closed everywhere else. Drives the REAL <see cref="SharedHostedWebApp"/> + the REAL
/// <see cref="NodeWebSessionAuthority"/> (real Argon2id hasher, real in-memory session store) over a
/// real in-process Kestrel loopback listener with a real <see cref="HttpClient"/>.
/// </summary>
public sealed class NodeWebSessionAuthorityTests
{
    private const string BootstrapToken = "test-per-boot-bootstrap-token-AAAA-BBBB";
    private const string FounderUser = "founder";
    private const string FounderPassword = "correct horse battery staple";

    // A gated stub at a node-local path — NOT allowlisted, so the gate decides whether the caller
    // reaches the 200 handler.
    private const string GatedPath = "/api/local-node/web-session-test-stub";

    // A gated stub that ALSO runs the PER-ROUTE defence-in-depth caller-auth check, exactly like the
    // real /api/local-node/* routes (SyncStatusRoutes / TeamRoutes / CommsRoutes /
    // CurrentPrincipalSignatureRoutes). This is the surface the #1842 web-client login bounced on:
    // the listener gate accepts the web session (Accept 2), but before the gate-passed-marker fix
    // this per-route check — which knows ONLY the bootstrap token — returned 401, and the browser
    // bounced back to the login screen on that 401.
    private const string PerRouteCheckedPath = "/api/local-node/per-route-checked-stub";

    private sealed class SingleTeamAccessor : IActiveTeamAccessor
    {
        public SingleTeamAccessor(TeamContext active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class ConfigurableV1AuthorityGate(bool isAllowed = true)
        : IInstallationIdentityV1AuthorityGate
    {
        public bool IsAllowed { get; set; } = isAllowed;

        public Task<InstallationIdentityV1MutationAdmission> CheckV1MutationAdmissionAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Admission());

        public Task<InstallationIdentityV1MutationAdmission> CheckLegacyBearerAdmissionAsync(
            InstallationIdentityLegacyBearerAudience audience,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Admission());

        private InstallationIdentityV1MutationAdmission Admission() =>
            IsAllowed
                ? new InstallationIdentityV1MutationAdmission(true, null)
                : new InstallationIdentityV1MutationAdmission(
                    false,
                    InstallationIdentityCutoverOrchestrator.LegacyAuthorityRetiredRefusal);
    }

    /// <summary>
    /// The v2 selected-audience logout authority, wired into the harness so the REAL
    /// <see cref="SessionLogoutRoutes"/> is exercised — and refusing every call. A legacy-audience
    /// sign-out that reached it would fail the request loudly, so these tests keep asserting the
    /// audience-separation property (#3343): a <c>__Host-web_session</c> holder is NEVER resolved
    /// through the selected-session authority.
    /// </summary>
    private sealed class RefusingSelectedLogoutAuthority : IWebSelectedSessionLogoutAuthority
    {
        internal static readonly RefusingSelectedLogoutAuthority Instance = new();

        public Task<bool> LogoutAsync(string? selectedHandle, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "audience violation: a legacy web-session sign-out reached the SELECTED logout authority.");
    }

    /// <summary>
    /// The antiforgery policy, wired in and refusing every call.
    /// </summary>
    /// <remarks>
    /// Two DIFFERENT reasons, deliberately not conflated. The AUTHENTICATED audiences (selected /
    /// challenge / installation, and the Rotate members) must never be reached by a legacy
    /// sign-out — that is a genuine audience violation and must stay a hard failure. The ANONYMOUS
    /// members are a different case: the anonymous audience is not a v2 audience, it is the one a
    /// v1-only holder is entitled to (<c>HasAnyAuthenticatedAudience</c> returns false for them), so
    /// consuming it here would be legitimate. It is refused only because this branch does not
    /// consume antiforgery TODAY. That gap is tracked as earlier repository ticket #3353, and it has to close together
    /// with the Harborline App sending the header — the client currently sends no request headers at all,
    /// so a server-side requirement alone would 400 every sign-out. When #3353 lands, these two
    /// members must be allowed rather than the finding re-litigated; this throw is the tripwire that
    /// will say so out loud.
    /// </remarks>
    private sealed class RefusingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        internal static readonly RefusingAntiforgeryPolicy Instance = new();

        private static InvalidOperationException Violation() =>
            new("audience violation: a legacy web-session sign-out consulted an AUTHENTICATED " +
                "antiforgery audience (selected / challenge / installation).");

        private static InvalidOperationException NotYetWired() =>
            new("the legacy branch consumes no antiforgery today (shipyard#3353). If this fires, " +
                "the gap was closed without updating this double — allow the anonymous members.");

        public Task<bool> IssueAnonymousAsync(HttpContext context) => throw NotYetWired();

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => throw NotYetWired();

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) => throw Violation();

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) => throw Violation();

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            throw Violation();

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) => throw Violation();

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) => throw Violation();

        public void EmitToken(HttpResponse response, string token) => throw NotYetWired();

        public void ExpireAnonymousBinding(HttpResponse response) => throw NotYetWired();
    }

    /// <summary>
    /// Captures the canonical <c>Auth.*</c> audit event types the authority emits (#3362).
    /// </summary>
    /// <remarks>
    /// The node is not the Bridge audit sink, so — per the ADR-0099 pre-sink state the authority
    /// documents — it emits these as STRUCTURED logging with the canonical label as the
    /// <c>{AuditEventType}</c> value. This provider reads that structured value rather than
    /// substring-matching the rendered message, so the assertions below pin the audit line's
    /// SUBJECT and not merely that some text was written.
    /// </remarks>
    private sealed class AuditCapture : ILoggerProvider
    {
        private readonly List<string> _eventTypes = [];
        private readonly Lock _gate = new();

        /// <summary>Every <c>{AuditEventType}</c> value logged so far, in order.</summary>
        internal IReadOnlyList<string> EventTypes
        {
            get { lock (_gate) { return [.. _eventTypes]; } }
        }

        internal int CountOf(string eventType) =>
            EventTypes.Count(e => string.Equals(e, eventType, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose() { }

        private void Record(string eventType)
        {
            lock (_gate) { _eventTypes.Add(eventType); }
        }

        private sealed class Sink(AuditCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    return;
                }
                foreach (var pair in values)
                {
                    if (string.Equals(pair.Key, "AuditEventType", StringComparison.Ordinal)
                        && pair.Value is string eventType)
                    {
                        owner.Record(eventType);
                    }
                }
            }
        }
    }

    private sealed record Harness(SharedHostedWebApp App, HttpClient Client, AuditCapture Audit)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }

    private static string HashOf(string password) =>
        new Argon2idPasswordHasher<NodeWebUser>(Options.Create(new Argon2idHashOptions()))
            .HashPassword(NodeWebUser.Instance, password);

    /// <summary>Build + start the shared app with the web-client stack wired, a gated stub, and /health.</summary>
    private static async Task<Harness> StartAsync(bool provisionCredential = true)
    {
        var outer = new ServiceCollection();
        var audit = new AuditCapture();
        outer.AddLogging(b => b.AddProvider(audit));
        outer.AddTestKernelClock();

        var team = new TeamContext(
            new TeamId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "Test Team",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        outer.AddSingleton<IActiveTeamAccessor>(new SingleTeamAccessor(team));
        outer.AddSingleton(new NodeCallerSessionToken(BootstrapToken));

        outer.Configure<NodeWebClientOptions>(o =>
        {
            o.Enabled = true;
            o.FounderUsername = provisionCredential ? FounderUser : null;
            o.FounderPasswordHash = provisionCredential ? HashOf(FounderPassword) : null;
        });
        outer.AddHarborlinePasswordHashing<NodeWebUser>();
        outer.AddHarborlineSessionEstablishment(); // reused ISessionStore + SessionOptions
        outer.AddSingleton<IInstallationIdentityV1AuthorityGate>(
            new ConfigurableV1AuthorityGate());
        outer.AddSingleton<WebLoginRateLimiter>();
        outer.AddSingleton<INodeWebSessionAuthority, NodeWebSessionAuthority>();

        var outerProvider = outer.BuildServiceProvider();
        var options = Options.Create(new LocalNodeOptions { HealthPort = 0 });
        var logger = outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>();

        var app = new SharedHostedWebApp(
            outerProvider,
            options,
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            logger,
            outerProvider.GetRequiredService<TimeProvider>());
        app.MapHealthCheckIfAbsent();

        var authority = outerProvider.GetRequiredService<INodeWebSessionAuthority>();
        var callerAuth = outerProvider.GetRequiredService<NodeCallerSessionToken>();
        app.MapApiRoutes(a =>
        {
            var preAuth = a.MapPreAuthOperationalGroup();
            var selectedSession = a.MapSelectedSessionProductGroup();
            WebSessionRoutes.Map(
                preAuth,
                selectedSession,
                authority,
                outerProvider.GetRequiredService<WebLoginRateLimiter>());
            // The REAL sign-out route, wired the way HostedWebSessionApiEndpoint wires it: the v2
            // selected-audience authority + antiforgery ALONGSIDE the v1 legacy authority. Both v2
            // collaborators throw on any call, so a legacy sign-out that leaked into the selected
            // audience fails loudly instead of silently passing (#3343 audience separation).
            SessionLogoutRoutes.Map(
                preAuth,
                RefusingSelectedLogoutAuthority.Instance,
                authority,
                RefusingAntiforgeryPolicy.Instance);
            a.MapGet(GatedPath, () => Results.Ok(new { ok = true }));
            // Mirror the real routes: a per-route defence-in-depth caller-auth check IN ADDITION to
            // the authoritative listener gate. Before the fix a web session was accepted by the gate
            // but rejected HERE (this check knew only the bootstrap token).
            a.MapGet(PerRouteCheckedPath, (HttpRequest req) =>
                callerAuth.Validate(req) is NodeCallerSessionToken.Decision.Reject
                    ? NodeCallerSessionToken.RejectResult()
                    : Results.Ok(new { ok = true }));
        });

        await app.StartAsync(CancellationToken.None);
        // UseCookies=false: the test drives the Cookie header MANUALLY (the auto CookieContainer
        // refuses to send a Secure cookie over the in-process HTTP loopback, and would mask the
        // server's own cookie read path). The server reads Request.Cookies by name regardless of the
        // browser-side Secure/__Host- send rules, so a manual Cookie header exercises it directly.
        var client = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(app.SelectedUrl!),
        };
        return new Harness(app, client, audit);
    }

    private static HttpRequestMessage Login(string username, string password) =>
        new(HttpMethod.Post, WebSessionRoutes.LoginPath)
        {
            Content = JsonContent.Create(new { username, password }),
        };

    private static HttpRequestMessage GetGated(string? bearer) => GetGated(bearer, GatedPath);

    private static HttpRequestMessage GetGated(string? bearer, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        return req;
    }

    private sealed record LoginBody(string Token, string User, string DisplayName, DateTimeOffset ExpiresAt);

    private static async Task<string> LoginAndGetTokenAsync(HttpClient client)
    {
        var resp = await client.SendAsync(Login(FounderUser, FounderPassword));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LoginBody>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        return body.Token;
    }

    [Fact(DisplayName = "Correct login mints a session the gate accepts on a gated route (login is allowlisted)")]
    public async Task CorrectLogin_MintsSession_GateAcceptsBearer()
    {
        await using var h = await StartAsync();

        // Login is reachable WITHOUT any prior auth (allowlisted) and succeeds → proves both.
        var token = await LoginAndGetTokenAsync(h.Client);

        // The minted per-user bearer is accepted by the listener gate on a gated route.
        var gated = await h.Client.SendAsync(GetGated(token));
        Assert.Equal(HttpStatusCode.OK, gated.StatusCode);
    }

    [Fact(DisplayName = "REGRESSION #1842: a web session reaches a route that ALSO keeps a per-route caller-auth check (was 401 → login bounce)")]
    public async Task CorrectLogin_WebSession_ReachesRoute_WithPerRouteCallerAuthCheck()
    {
        // This is the exact surface CIC's live login bounced on: POST /api/session/login → 200, then
        // the FIRST guarded mount-time call (GET /api/local-node/sync-status) returned 401 — not from
        // the listener gate (no gate-rejection log) but from the route's OWN per-route caller-auth
        // check, which knew only the bootstrap token. The browser's nodeFetch treated that 401 as an
        // expired session, cleared the token, and bounced the WebSessionGate back to the login screen
        // (with the URL already redirected to #/inbox/). The gate-passed marker makes the per-route
        // check trust the authoritative gate's Accept-2 decision.
        await using var h = await StartAsync();
        var token = await LoginAndGetTokenAsync(h.Client);

        var perRoute = await h.Client.SendAsync(GetGated(token, PerRouteCheckedPath));
        Assert.Equal(HttpStatusCode.OK, perRoute.StatusCode); // was Unauthorized (401) before the fix

        // The bootstrap token still reaches the same per-route-checked route (desktop path unchanged).
        var bootstrap = await h.Client.SendAsync(GetGated(BootstrapToken, PerRouteCheckedPath));
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
    }

    [Fact(DisplayName = "Wrong password is rejected 401 (fail-closed, non-enumerating)")]
    public async Task WrongPassword_Is401()
    {
        await using var h = await StartAsync();
        var resp = await h.Client.SendAsync(Login(FounderUser, "not the password"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact(DisplayName = "Unknown username is rejected 401 (same 401 as wrong password — non-enumerating)")]
    public async Task UnknownUsername_Is401()
    {
        await using var h = await StartAsync();
        var resp = await h.Client.SendAsync(Login("nobody", FounderPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact(DisplayName = "No founder credential provisioned ⇒ login is FAIL-CLOSED (401)")]
    public async Task NoCredentialProvisioned_FailClosed_Is401()
    {
        await using var h = await StartAsync(provisionCredential: false);
        var resp = await h.Client.SendAsync(Login(FounderUser, FounderPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact(DisplayName = "A gated route is 401 without any bearer, and with a bogus web bearer")]
    public async Task GatedRoute_NoOrBogusBearer_Is401()
    {
        await using var h = await StartAsync();

        var noAuth = await h.Client.SendAsync(GetGated(null));
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        var bogus = await h.Client.SendAsync(GetGated("not-a-real-session-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);
    }

    [Fact(DisplayName = "The bootstrap per-boot token still passes the gate alongside web sessions")]
    public async Task BootstrapToken_StillAccepted()
    {
        await using var h = await StartAsync();
        var gated = await h.Client.SendAsync(GetGated(BootstrapToken));
        Assert.Equal(HttpStatusCode.OK, gated.StatusCode);
    }

    /// <summary>The <c>Set-Cookie</c> values on a response (empty when none were written).</summary>
    private static string[] SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];

    private static HttpRequestMessage Logout() =>
        new(HttpMethod.Post, SessionLogoutRoutes.LogoutPath);

    [Fact(DisplayName =
        "#3343: a legacy BEARER holder signs out — the node REVOKES server-side, and the selected " +
        "audience is never consulted")]
    public async Task LegacyBearerLogout_RevokesServerSide_WithoutTouchingTheSelectedAudience()
    {
        await using var h = await StartAsync();
        var token = await LoginAndGetTokenAsync(h.Client);

        // Works before logout.
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(GetGated(token))).StatusCode);

        var logout = Logout();
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var logoutResp = await h.Client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);
        Assert.True(logoutResp.Headers.CacheControl?.NoStore);

        // THE load-bearing assertion (#3343): the session is gone SERVER-SIDE, so the very same
        // credential no longer opens a gated route. A sign-out that leaves the session live is a
        // security defect, not a UX one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(GetGated(token))).StatusCode);

        // Audience separation still holds. The selected-session authority and the v2 antiforgery
        // policy both throw on any call, so reaching this line proves neither was consulted; and no
        // selected-audience cookie was written.
        Assert.DoesNotContain(
            SetCookies(logoutResp),
            cookie => cookie.Contains("__Host-hl-", StringComparison.Ordinal));
    }

    [Fact(DisplayName =
        "#3343: sign-out is idempotent — a second attempt with an already-dead credential still " +
        "succeeds, so a locked sign-out screen can recover without a reload")]
    public async Task LegacyLogout_IsIdempotent_SoALockedGateCanRecover()
    {
        await using var h = await StartAsync();
        var token = await LoginAndGetTokenAsync(h.Client);

        var first = Logout();
        first.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.SendAsync(first)).StatusCode);

        // The record is already gone. A retry (the "Retry sign out" button) must still confirm
        // rather than 401 forever — that permanent 401 is what made the locked state reload-only.
        var retry = Logout();
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.SendAsync(retry)).StatusCode);
    }

    [Fact(DisplayName =
        "#3343 F3: a bearer does not SHADOW the cookie — a request presenting both revokes both, " +
        "so a 204 never claims a sign-out that left the cookie's session live")]
    public async Task Logout_WithBearerAndCookie_RevokesBoth_NotJustTheFirstCredential()
    {
        await using var h = await StartAsync();

        // A live cookie session...
        var loginResp = await h.Client.SendAsync(Login(FounderUser, FounderPassword));
        var cookie = ReadSessionCookie(loginResp);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(GetGatedWithCookie(cookie, GatedPath))).StatusCode);

        // ...presented ALONGSIDE a stale/junk bearer. ExtractSessionToken tries the bearer FIRST,
        // so examining only the first credential would revoke nothing while still answering 204.
        var logout = Logout();
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stale-or-junk-session-id");
        logout.Headers.Add("Cookie", $"{NodeWebSessionAuthority.SessionCookieName}={cookie}");
        var logoutResp = await h.Client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        // The 204 must be true of the credential that actually had a session behind it.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await h.Client.SendAsync(GetGatedWithCookie(cookie, GatedPath))).StatusCode);
    }

    /// <summary>The canonical sign-out audit event type (<see cref="AuditEventTypes.SignedOut"/>).</summary>
    private const string SignedOutEventType = "Auth.SignedOut";

    [Fact(DisplayName =
        "#3362: a sign-out that revokes a live session EMITS the Auth.SignedOut audit line")]
    public async Task Logout_ThatRevokesASession_EmitsTheSignedOutAuditLine()
    {
        await using var h = await StartAsync();
        var token = await LoginAndGetTokenAsync(h.Client);
        Assert.Equal(0, h.Audit.CountOf(SignedOutEventType));

        var logout = Logout();
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.SendAsync(logout)).StatusCode);

        // The SUBJECT, not merely "nothing threw": the canonical Auth.SignedOut label was emitted,
        // exactly once, as the structured {AuditEventType} value.
        Assert.Equal(1, h.Audit.CountOf(SignedOutEventType));
    }

    [Fact(DisplayName =
        "#3362: a MULTI-credential sign-out where the EARLIER credential is the one that revoked " +
        "still emits Auth.SignedOut — this is what the `revoked |=` accumulator exists for")]
    public async Task Logout_WhenAnEarlierCredentialIsTheOneRevoked_StillEmitsTheSignedOutAuditLine()
    {
        await using var h = await StartAsync();

        // A LIVE bearer presented alongside a stale/junk cookie. LogoutAsync walks the credentials
        // bearer-FIRST, so the revoking credential is the EARLIER one and the last store remove
        // returns false. `revoked |=` keeps the true; `revoked =` would let the trailing false
        // overwrite it and silently skip the audit line for a sign-out that really did revoke.
        var token = await LoginAndGetTokenAsync(h.Client);
        var logout = Logout();
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        logout.Headers.Add(
            "Cookie",
            $"{NodeWebSessionAuthority.SessionCookieName}=stale-or-junk-session-id");

        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.SendAsync(logout)).StatusCode);

        // The revocation really happened — the bearer no longer opens a gated route...
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.SendAsync(GetGated(token))).StatusCode);
        // ...so the audit line that records it must have been emitted.
        Assert.Equal(1, h.Audit.CountOf(SignedOutEventType));
    }

    [Fact(DisplayName =
        "#3362: a sign-out that revoked NOTHING emits no Auth.SignedOut line — the audit records a " +
        "revocation, so an unconditional emission would be a false audit record")]
    public async Task Logout_ThatRevokedNothing_EmitsNoSignedOutAuditLine()
    {
        await using var h = await StartAsync();
        var token = await LoginAndGetTokenAsync(h.Client);

        var first = Logout();
        first.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.SendAsync(first)).StatusCode);
        Assert.Equal(1, h.Audit.CountOf(SignedOutEventType));

        // The idempotent retry answers 204 (a locked gate must be able to recover) but revokes
        // nothing, because the record is already gone. Without this case the gating condition could
        // be replaced by an unconditional emission and the tests above would still pass.
        var retry = Logout();
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.SendAsync(retry)).StatusCode);

        Assert.Equal(1, h.Audit.CountOf(SignedOutEventType));
    }

    [Fact(DisplayName =
        "#3343: sign-out with NO credential of any audience is 401 and mutates no cookie")]
    public async Task Logout_WithNoCredential_Is401_AndWritesNoCookie()
    {
        await using var h = await StartAsync();

        var logoutResp = await h.Client.SendAsync(Logout());

        Assert.Equal(HttpStatusCode.Unauthorized, logoutResp.StatusCode);
        Assert.Empty(SetCookies(logoutResp));
    }

    // ------------------------------------------------------------------------------------------
    // #131 — HttpOnly+Secure+SameSite=Strict cookie session (refresh-surviving web-client login).
    // ------------------------------------------------------------------------------------------

    /// <summary>Read + attribute-check the session cookie from a login response; returns its value.</summary>
    private static string ReadSessionCookie(HttpResponseMessage resp)
    {
        Assert.True(resp.Headers.TryGetValues("Set-Cookie", out var cookies), "expected a Set-Cookie header");
        var setCookie = cookies!.FirstOrDefault(
            c => c.StartsWith(NodeWebSessionAuthority.SessionCookieName + "=", StringComparison.Ordinal));
        Assert.NotNull(setCookie);

        var lower = setCookie!.ToLowerInvariant();
        Assert.Contains("httponly", lower);           // JS cannot read it (XSS-safe)
        Assert.Contains("secure", lower);             // HTTPS-only
        Assert.Contains("samesite=strict", lower);    // CSRF defense
        Assert.Contains("path=/", lower);             // host-scoped (required by __Host-)

        var afterName = setCookie[(NodeWebSessionAuthority.SessionCookieName.Length + 1)..];
        var semi = afterName.IndexOf(';');
        var value = semi >= 0 ? afterName[..semi] : afterName;
        Assert.False(string.IsNullOrWhiteSpace(value));
        return value;
    }

    private static HttpRequestMessage GetGatedWithCookie(string cookieValue, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{NodeWebSessionAuthority.SessionCookieName}={cookieValue}");
        return req;
    }

    [Fact(DisplayName = "#131: login sets an HttpOnly+Secure+SameSite=Strict cookie the gate accepts (incl. a per-route-checked route)")]
    public async Task CorrectLogin_SetsSecureHttpOnlyStrictCookie_GateAcceptsIt()
    {
        await using var h = await StartAsync();

        var loginResp = await h.Client.SendAsync(Login(FounderUser, FounderPassword));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var cookie = ReadSessionCookie(loginResp);

        // The cookie alone (no Authorization header) passes the listener gate on a gated route.
        var gated = await h.Client.SendAsync(GetGatedWithCookie(cookie, GatedPath));
        Assert.Equal(HttpStatusCode.OK, gated.StatusCode);

        // ...and reaches a route that ALSO keeps a per-route caller-auth check — the gate-passed
        // marker (#1842) is stamped for the cookie path exactly as for the bearer path.
        var perRoute = await h.Client.SendAsync(GetGatedWithCookie(cookie, PerRouteCheckedPath));
        Assert.Equal(HttpStatusCode.OK, perRoute.StatusCode);
    }

    [Fact(DisplayName =
        "#3343/#131: a legacy COOKIE holder signs out — REVOKED server-side, and only the legacy " +
        "cookie is expired (attribute-matching), never a selected-audience cookie")]
    public async Task LegacyCookieLogout_RevokesServerSide_AndExpiresOnlyTheLegacyCookie()
    {
        await using var h = await StartAsync();

        var loginResp = await h.Client.SendAsync(Login(FounderUser, FounderPassword));
        var cookie = ReadSessionCookie(loginResp);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(GetGatedWithCookie(cookie, GatedPath))).StatusCode);

        var logout = Logout();
        logout.Headers.Add("Cookie", $"{NodeWebSessionAuthority.SessionCookieName}={cookie}");
        var logoutResp = await h.Client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        // Revoked server-side: the same cookie no longer opens a gated route.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await h.Client.SendAsync(GetGatedWithCookie(cookie, GatedPath))).StatusCode);

        // An attribute-matching expired Set-Cookie for the LEGACY cookie only (a __Host- cookie is
        // dropped by the browser only when Secure/Path/SameSite match the mint).
        var expired = SetCookies(logoutResp).SingleOrDefault(
            c => c.StartsWith(NodeWebSessionAuthority.SessionCookieName + "=", StringComparison.Ordinal));
        Assert.NotNull(expired);
        var lower = expired!.ToLowerInvariant();
        Assert.Contains("expires=", lower, StringComparison.Ordinal);
        Assert.Contains("httponly", lower, StringComparison.Ordinal);
        Assert.Contains("secure", lower, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", lower, StringComparison.Ordinal);
        Assert.Contains("path=/", lower, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SetCookies(logoutResp),
            c => c.Contains("__Host-hl-", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "#131: a gated route is 401 with a bogus (non-session) cookie")]
    public async Task GatedRoute_BogusCookie_Is401()
    {
        await using var h = await StartAsync();
        var bogus = await h.Client.SendAsync(GetGatedWithCookie("not-a-real-session-id", GatedPath));
        Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);
    }

    /// <summary>A settable clock so an expiry test can advance past the session's idle window.</summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static DefaultHttpContext CookieContext(string sessionId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{NodeWebSessionAuthority.SessionCookieName}={sessionId}";
        return ctx;
    }

    [Fact(DisplayName = "#131: a cookie session is enforced server-side — it expires past the idle window")]
    public async Task CookieSession_ExpiresServerSide_PastIdleWindow()
    {
        // Drive the authority DIRECTLY with a controllable clock + the real in-memory store + real
        // Argon2id hasher (no Kestrel needed to prove the TTL gate).
        var team = new TeamContext(
            new TeamId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "Test Team",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-06T12:00:00Z"));
        var authority = new NodeWebSessionAuthority(
            Options.Create(new NodeWebClientOptions
            {
                Enabled = true,
                FounderUsername = FounderUser,
                FounderPasswordHash = HashOf(FounderPassword),
            }),
            new Argon2idPasswordHasher<NodeWebUser>(Options.Create(new Argon2idHashOptions())),
            new InMemorySessionStore(),
            // Fully-qualified: Microsoft.AspNetCore.Builder also exposes a SessionOptions (CS0104).
            Options.Create(new Harborline.Api.Foundation.Session.SessionOptions()), // 8h absolute / 30min idle floors
            new SingleTeamAccessor(team),
            new ConfigurableV1AuthorityGate(),
            clock,
            NullLogger<NodeWebSessionAuthority>.Instance);

        var login = (await authority.LoginAsync(FounderUser, FounderPassword, CancellationToken.None)).Login;
        Assert.NotNull(login);

        // Fresh: the cookie authenticates.
        Assert.True(await authority.TryAuthenticateAsync(CookieContext(login!.Token)));

        // Advance past the 30-minute sliding-idle window with no activity → server-side expired+reaped.
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(await authority.TryAuthenticateAsync(CookieContext(login.Token)));
    }

    /// <summary>
    /// A selected-audience logout authority that CONFIRMS the revocation, so the selected branch of
    /// <see cref="SessionLogoutRoutes"/> proceeds to its 204 exactly as it does in production for a
    /// browser holding a live v2 session.
    /// </summary>
    private sealed class AcceptingSelectedLogoutAuthority : IWebSelectedSessionLogoutAuthority
    {
        public Task<bool> LogoutAsync(string? selectedHandle, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// Allows ONLY the selected-audience consume the selected branch legitimately performs. Every
    /// other member still throws, so this double cannot quietly widen the audience separation the
    /// surrounding tests exist to protect.
    /// </summary>
    private sealed class SelectedOnlyAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        private static InvalidOperationException Violation() =>
            new("audience violation: the selected-logout branch consulted an unexpected antiforgery audience.");

        public Task<bool> IssueAnonymousAsync(HttpContext context) => throw Violation();

        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => throw Violation();

        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) => throw Violation();

        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);

        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            throw Violation();

        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) => throw Violation();

        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) => throw Violation();

        public void EmitToken(HttpResponse response, string token) => throw Violation();

        public void ExpireAnonymousBinding(HttpResponse response) => throw Violation();
    }

    /// <summary>Builds the real authority over a caller-owned store so a test can inspect the record.</summary>
    private static NodeWebSessionAuthority AuthorityOver(
        ISessionStore store,
        IInstallationIdentityV1AuthorityGate gate)
    {
        var team = new TeamContext(
            new TeamId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "Test Team",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        return new NodeWebSessionAuthority(
            Options.Create(new NodeWebClientOptions
            {
                Enabled = true,
                FounderUsername = FounderUser,
                FounderPasswordHash = HashOf(FounderPassword),
            }),
            new Argon2idPasswordHasher<NodeWebUser>(Options.Create(new Argon2idHashOptions())),
            store,
            Options.Create(new Harborline.Api.Foundation.Session.SessionOptions()),
            new SingleTeamAccessor(team),
            gate,
            TimeProvider.System,
            NullLogger<NodeWebSessionAuthority>.Instance);
    }

    [Fact(DisplayName =
        "#3245 F1: a post-cutover sign-out presenting BOTH audiences REVOKES the legacy record — a " +
        "204 over a surviving record is a false claim and leaves it open to resurrection")]
    [Trait("PlanCard", "MTW-01E")]
    public async Task PostCutoverLogout_PresentingBothAudiences_RevokesTheLegacyRecord()
    {
        var gate = new ConfigurableV1AuthorityGate();
        var store = new InMemorySessionStore();
        var authority = AuthorityOver(store, gate);

        var login = (await authority.LoginAsync(FounderUser, FounderPassword, CancellationToken.None)).Login;
        Assert.NotNull(login);
        // The ADMITTING state first: without a live record here the revocation assertion below would
        // pass over an authority that never stored anything.
        Assert.NotNull(await store.GetAsync(login!.Token, CancellationToken.None));

        // The cutover commits. Every legacy audience is retired from this point on.
        gate.IsAllowed = false;

        // One browser, both credentials — the selected branch of the real route handles this.
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie =
            $"{WebSessionCookieNames.Selected}=selected-handle; " +
            $"{NodeWebSessionAuthority.SessionCookieName}={login.Token}";

        var result = await SessionLogoutRoutes.LogoutAsync(
            new AcceptingSelectedLogoutAuthority(),
            authority,
            new SelectedOnlyAntiforgeryPolicy(),
            context);

        // The route reports a completed sign-out... (read off the result itself: executing it would
        // need a request-services container this bare context does not have, and the status is the
        // property under test, not the write mechanics).
        Assert.Equal(
            StatusCodes.Status204NoContent,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

        // ...so the legacy record it was asked to revoke MUST be gone. Revocation is destructive and
        // therefore always safe; refusing it because the legacy authority is retired PRESERVES the
        // very credential the cutover exists to dispose of.
        Assert.Null(await store.GetAsync(login.Token, CancellationToken.None));
    }

    [Fact]
    [Trait("PlanCard", "MTW-01E")]
    public async Task Committed_V2_Marker_Rejects_Legacy_Cookie_Tooling_And_New_Login()
    {
        var team = new TeamContext(
            new TeamId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            "Test Team",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        var gate = new ConfigurableV1AuthorityGate();
        var authority = new NodeWebSessionAuthority(
            Options.Create(new NodeWebClientOptions
            {
                Enabled = true,
                FounderUsername = FounderUser,
                FounderPasswordHash = HashOf(FounderPassword),
            }),
            new Argon2idPasswordHasher<NodeWebUser>(Options.Create(new Argon2idHashOptions())),
            new InMemorySessionStore(),
            Options.Create(new Harborline.Api.Foundation.Session.SessionOptions()),
            new SingleTeamAccessor(team),
            gate,
            TimeProvider.System,
            NullLogger<NodeWebSessionAuthority>.Instance);
        var login = (await authority.LoginAsync(
            FounderUser,
            FounderPassword,
            CancellationToken.None)).Login;
        Assert.NotNull(login);

        gate.IsAllowed = false;
        var tooling = new DefaultHttpContext();
        tooling.Request.Headers.Authorization = $"Bearer {login!.Token}";

        Assert.False(await authority.TryAuthenticateAsync(CookieContext(login.Token)));
        Assert.False(await authority.TryAuthenticateAsync(tooling));
        Assert.Null(await authority.DescribeAsync(
            CookieContext(login.Token),
            CancellationToken.None));
        Assert.Null((await authority.LoginAsync(
            FounderUser,
            FounderPassword,
            CancellationToken.None)).Login);
    }
}
