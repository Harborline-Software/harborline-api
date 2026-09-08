using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// inc-4 F1 — the STRUCTURAL proof that the loopback caller-auth gap is closed at the
/// LISTENER level. inc-4 (#1286) gated only 3 route groups per-route (opt-in), leaving the
/// UNAUTHENTICATED financial-cluster write routes (journal-entry, invoice, bill, payment, …)
/// reachable by any local process WITHOUT the token. This suite proves the
/// <see cref="SharedHostedWebApp"/> caller-auth MIDDLEWARE now gates EVERY route by default:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>A financial WRITE path (<c>POST /api/local-node/journal-entries</c>) is REJECTED 401
///     without the token and ACCEPTED with it — the gap-closing proof. PRE-FIX this exact path
///     had NO per-route auth (the JournalEntry handler binds a body, no <c>HttpRequest</c>
///     caller-auth check) so the request would have reached the handler unauthenticated.</item>
///   <item>A NEW / arbitrary route is gated-by-default — the middleware covers it WITHOUT any
///     per-route opt-in (the forgotten-route problem, structurally solved).</item>
///   <item>The allowlisted routes (<c>/health</c> liveness, <c>/ws</c> peer-sync) remain
///     reachable — they are NOT 401 (each is authenticated by its own layer).</item>
///   <item>Dev/single-host parity: with NO token configured every route is reachable (the
///     un-enforced fallback the shipped Harborline App never hits).</item>
/// </list>
/// <para>
/// Drives the REAL <see cref="SharedHostedWebApp"/> (its real middleware + ConfigureUrls)
/// over a real in-process Kestrel loopback listener with a real <see cref="HttpClient"/>.
/// The route HANDLERS are trivial 200 stubs: the gate is PATH-based and runs BEFORE the
/// handler, so a stub at the real financial path proves the gate exactly — the financial
/// handler's internals are irrelevant to whether the LISTENER turns the caller away.
/// </para>
/// </remarks>
public sealed class SharedHostedWebAppCallerAuthTests
{
    private const string Token = "test-per-boot-session-token-AAAA-BBBB-CCCC";

    // Real financial WRITE path (JournalEntryRoutes.RouteBase) — the gap inc-4 F1 closes.
    private const string FinancialWritePath = "/api/local-node/journal-entries";

    // A brand-new route that NO ONE added a per-route check to — gated by default.
    private const string ArbitraryNewPath = "/api/local-node/arbitrary-new-route";

    /// <summary>
    /// Minimal <see cref="IActiveTeamAccessor"/> for the inner health check — no team is
    /// active (the health probe tolerates this; the caller-auth gate is what's under test).
    /// </summary>
    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    /// <summary>No-op sync acceptor so <c>/ws</c> can be mapped without the real pipeline.</summary>
    private static async Task NoopWsAccept(WebSocket ws, CancellationToken ct)
    {
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", ct).ConfigureAwait(false);
        }
        catch
        {
            // The non-WebSocket GET path in MapWebSocketPath returns 400 before this runs.
        }
    }

    /// <summary>
    /// Build + start a real <see cref="SharedHostedWebApp"/> with <paramref name="sessionToken"/>
    /// (null ⇒ dev/un-enforced), mapping the financial-write stub, the arbitrary stub,
    /// <c>/health</c>, and <c>/ws</c>. Returns the started app + an HttpClient bound to it.
    /// </summary>
    private static async Task<(SharedHostedWebApp App, HttpClient Client)> StartAsync(string? sessionToken)
    {
        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging();
        outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        outer.AddSingleton(new NodeCallerSessionToken(sessionToken));
        var outerProvider = outer.BuildServiceProvider();

        var options = Options.Create(new LocalNodeOptions { HealthPort = 0 });
        var logger = outerProvider.GetRequiredService<
            Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>();

        var app = new SharedHostedWebApp(
            outerProvider,
            options,
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            logger,
            outerProvider.GetRequiredService<TimeProvider>());

        app.MapHealthCheckIfAbsent();
        app.MapWebSocketPath("/ws", NoopWsAccept);
        app.MapApiRoutes(a =>
        {
            // Trivial stubs at the REAL financial path + a brand-new path. The handlers have
            // NO caller-auth of their own — exactly like the real JournalEntry POST handler.
            a.MapDeviceReachableProductDataGroup().MapPost(
                FinancialWritePath,
                () => Results.Ok(new { posted = true }));
            a.MapPost(ArbitraryNewPath, () => Results.Ok(new { ok = true }));
        });

        await app.StartAsync(CancellationToken.None);

        var baseUrl = app.SelectedUrl!;
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (app, client);
    }

    private static async Task<(SharedHostedWebApp App, HttpClient Client)> StartWithWebClientAsync(
        string bundleRoot,
        bool enabled)
    {
        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging();
        outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        outer.AddSingleton(new NodeCallerSessionToken(Token));
        outer.AddSingleton<IOptions<NodeWebClientOptions>>(
            Options.Create(new NodeWebClientOptions
            {
                Enabled = enabled,
                BundleRoot = bundleRoot,
            }));
        var outerProvider = outer.BuildServiceProvider();

        var app = new SharedHostedWebApp(
            outerProvider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            outerProvider.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
            routes.MapGet("/api/static-hosting-probe", () => Results.Ok()));
        await app.StartAsync(CancellationToken.None);

        var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        return (app, client);
    }

    private static HttpRequestMessage Post(string path, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { }),
        };
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        return req;
    }

    [Fact(DisplayName = "Executable evidence seals the actual route graph before the listener binds")]
    public async Task ExecutableRegistry_Seals_Actual_RouteGraph()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var snapshot = app.ExecutableEndpointRegistry.Current;
            Assert.True(app.ExecutableEndpointRegistry.IsSealed);
            Assert.True(snapshot.ListenerCallerAuthEnforced);
            Assert.False(snapshot.PublicStaticFilesEnabled);

            AssertEndpoint(
                snapshot,
                "/health",
                "*",
                NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy);
            AssertEndpoint(
                snapshot,
                "/ws",
                "*",
                NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy);
            AssertEndpoint(
                snapshot,
                FinancialWritePath,
                "POST",
                NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy);
            AssertEndpoint(
                snapshot,
                ArbitraryNewPath,
                "POST",
                NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy);

            Assert.Throws<ObjectDisposedException>(() =>
                app.MapApiRoutes(routes => routes.MapGet("/too-late", () => Results.Ok())));
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Endpoint mapping and sealing share one atomic lifecycle gate")]
    public async Task ExecutableRegistry_Includes_A_Concurrent_InFlight_Mapper()
    {
        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging();
        outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        outer.AddSingleton(new NodeCallerSessionToken(Token));
        await using var outerProvider = outer.BuildServiceProvider();

        var registry = new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry();
        var app = new SharedHostedWebApp(
            outerProvider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            registry,
            outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            outerProvider.GetRequiredService<TimeProvider>());
        var mapperEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMapper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var mapping = Task.Run(() => app.MapApiRoutes(routes =>
            {
                mapperEntered.SetResult();
                releaseMapper.Task.GetAwaiter().GetResult();
                routes.MapGet("/mapped-before-seal", () => Results.Ok());
            }));

            await mapperEntered.Task;
            var starting = Task.Run(() => app.StartAsync(CancellationToken.None));
            Assert.False(starting.IsCompleted);

            releaseMapper.SetResult();
            await mapping;
            await starting;

            Assert.Contains(
                registry.Current.Endpoints,
                endpoint => endpoint.RoutePattern == "/mapped-before-seal");
        }
        finally
        {
            releaseMapper.TrySetResult();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Only the reviewed WebSocket allowlist entry admits descendants")]
    public async Task Exact_Allowlist_Entries_Do_Not_Admit_Descendants()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/health/")).StatusCode);
            Assert.NotEqual(
                HttpStatusCode.Unauthorized,
                (await client.GetAsync("/api/session/login/")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health/details")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.GetAsync("/api/session/login/reset")).StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Static evidence is true only when the public bundle middleware is installed")]
    public async Task ExecutableRegistry_Records_Installed_Public_Static_Middleware()
    {
        var root = Path.Combine(Path.GetTempPath(), $"harborline-static-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "probe.txt"), "public probe");

        SharedHostedWebApp? app = null;
        HttpClient? client = null;
        try
        {
            (app, client) = await StartWithWebClientAsync(root, enabled: true);

            Assert.True(app.ExecutableEndpointRegistry.Current.PublicStaticFilesEnabled);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe.txt")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await client.GetAsync("/api/static-hosting-probe")).StatusCode);
        }
        finally
        {
            client?.Dispose();
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Missing public bundle root records static middleware as absent")]
    public async Task ExecutableRegistry_Records_Missing_Public_Static_Root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"harborline-missing-static-{Guid.NewGuid():N}");
        var (app, client) = await StartWithWebClientAsync(root, enabled: true);
        try
        {
            Assert.False(app.ExecutableEndpointRegistry.Current.PublicStaticFilesEnabled);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Disabled public bundle profile records static middleware as absent")]
    public async Task ExecutableRegistry_Records_Disabled_Public_Static_Profile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"harborline-disabled-static-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        SharedHostedWebApp? app = null;
        HttpClient? client = null;
        try
        {
            (app, client) = await StartWithWebClientAsync(root, enabled: false);
            Assert.False(app.ExecutableEndpointRegistry.Current.PublicStaticFilesEnabled);
        }
        finally
        {
            client?.Dispose();
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "F1 gap closed: financial WRITE route is 401 WITHOUT the token (would pass unauthenticated pre-fix)")]
    public async Task FinancialWrite_NoToken_Is401()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var resp = await client.SendAsync(Post(FinancialWritePath));
            // PRE-FIX: the JournalEntry POST handler has NO caller-auth, so this reached the
            // handler (200/4xx-from-handler) unauthenticated. POST-FIX: the listener gate
            // rejects it fail-closed BEFORE the handler runs.
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "F1 gap closed: financial WRITE route is ACCEPTED (not 401) WITH the token")]
    public async Task FinancialWrite_WithToken_NotRejected()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var resp = await client.SendAsync(Post(FinancialWritePath, Token));
            // The valid bearer passes the gate; the (stub) handler then returns 200. The point
            // is the gate did NOT 401 it — pair with the no-token 401 above to prove the gate.
            Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Gate-by-default: a brand-new route with NO per-route opt-in is 401 without the token")]
    public async Task ArbitraryNewRoute_NoToken_Is401()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var resp = await client.SendAsync(Post(ArbitraryNewPath));
            // No one added a per-route check to this route. The listener middleware gates it
            // anyway — the structural fix for the forgotten-route problem.
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

            // And it passes WITH the token (proving the gate is the only thing that 401'd it).
            var ok = await client.SendAsync(Post(ArbitraryNewPath, Token));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Challenge audience cannot authorize an arbitrary business route")]
    public async Task ArbitraryNewRoute_ChallengeCookie_Is401()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var request = Post(ArbitraryNewPath);
            request.Headers.Add("Cookie", "__Host-hl-challenge=valid-looking-challenge-handle");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(
        "__Host-hl-selected=expired-selected; __Host-web_session=legacy-live",
        "Selected")]
    [InlineData(
        "__Host-hl-challenge=challenge-handle; __Host-web_session=legacy-live",
        "Foreign")]
    [InlineData(
        "__Host-hl-install=installation-handle; __Host-web_session=legacy-live",
        "Foreign")]
    [InlineData(
        "__Host-web_session=legacy-live",
        "LegacyEligible")]
    [Trait("PlanCard", "MTW-01C")]
    public void Cookie_Audience_Disposition_Prevents_Cross_Audience_Legacy_Fallback(
        string cookieHeader,
        string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = cookieHeader;

        Assert.Equal(
            expected,
            SharedHostedWebApp.ClassifyWebCookieAudience(context.Request).ToString());
    }

    [Fact(DisplayName = "Wrong token is rejected 401 (fail-closed) on the financial path")]
    public async Task FinancialWrite_WrongToken_Is401()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var resp = await client.SendAsync(Post(FinancialWritePath, "the-wrong-token"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Allowlist: /health is reachable WITHOUT the token (liveness probe, not 401)")]
    public async Task Health_NoToken_NotRejected()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var resp = await client.GetAsync("/health");
            // /health is allowlisted — the Bridge supervisor probes it before the Harborline App (and
            // its bearer) exists. It must NOT be 401. (Status is 200/503 health, never 401.)
            Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Allowlist: /ws is reachable WITHOUT the token — a plain GET hits the WS handler (400, not 401)")]
    public async Task Ws_NoToken_NotRejected()
    {
        var (app, client) = await StartAsync(Token);
        try
        {
            var resp = await client.GetAsync("/ws");
            // /ws is allowlisted (peer sync authenticates at its own trust layer). A plain
            // (non-upgrade) GET reaches the WS handler, which returns 400 BadRequest. If the
            // gate had caught /ws it would be 401 — so 400 proves the allowlist bypass.
            Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Dev/single-host parity: NO token configured ⇒ legacy routes stay reachable while a new unclassified route is refused")]
    public async Task NoTokenConfigured_LegacyRoutesReachable_ButNewUnclassifiedRouteRefused()
    {
        var (app, client) = await StartAsync(sessionToken: null);
        try
        {
            Assert.False(app.ExecutableEndpointRegistry.Current.ListenerCallerAuthEnforced);
            // Financial write — reachable (the shipped Harborline App always injects a token; this is
            // the dev/test/Bridge-tenant fallback the node logs loudly at startup).
            var fin = await client.SendAsync(Post(FinancialWritePath));
            Assert.Equal(HttpStatusCode.OK, fin.StatusCode);

            // A newly invented, unclassified route has no legacy standing. Ticket 066's request-time
            // safe default remains active even when the older listener caller-auth gate is disabled.
            var arb = await client.SendAsync(Post(ArbitraryNewPath));
            Assert.Equal(HttpStatusCode.Forbidden, arb.StatusCode);
            using var body = JsonDocument.Parse(await arb.Content.ReadAsStringAsync());
            Assert.Equal(
                "route-audience.unclassified",
                body.RootElement.GetProperty("code").GetString());

            // /health reachable.
            var health = await client.GetAsync("/health");
            Assert.NotEqual(HttpStatusCode.Unauthorized, health.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Allowlist parity: recovery-accept is reachable WITHOUT the caller bearer and refuses a bad/expired code with one non-enumerating 401 (Finding 3, #3013)")]
    public async Task RecoveryAccept_NoCallerBearer_Reachable_And_NonEnumerating()
    {
        var (app, client) = await StartWithRecoveryRouteAsync();
        try
        {
            // Reachable without the caller bearer: recovery runs when the human is credential-broken,
            // so the single-use code is the only authority — parity with account-setup-accept. A VALID
            // code returns 200, proving the LISTENER allowlist let the request reach the handler rather
            // than 401'ing it at the gate. (Remove the allowlist entry ⇒ the gate 401s this and the
            // assertion fails — this test guards Finding 3's fix.)
            var recovered = await client.SendAsync(PostRecovery("good-recovery-code"));
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

            // Non-enumerating refusal: an unknown code and an expired/inactive-account code return the
            // IDENTICAL generic 401, so the surface never reveals whether the code or the target
            // account was the reason.
            var unknown = await client.SendAsync(PostRecovery("unknown-code"));
            var expired = await client.SendAsync(PostRecovery("expired-recovery-code"));
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
            Assert.Equal(
                await unknown.Content.ReadAsStringAsync(),
                await expired.Content.ReadAsStringAsync());
        }
        finally
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Start the REAL listener (token enforced) mapping the REAL <see cref="RecoveryAcceptRoutes"/>
    /// over a stub recovery authority + passthrough antiforgery — so the caller-auth gate is what
    /// decides whether a no-bearer request reaches the handler.
    /// </summary>
    private static async Task<(SharedHostedWebApp App, HttpClient Client)> StartWithRecoveryRouteAsync()
    {
        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging();
        outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        outer.AddSingleton(new NodeCallerSessionToken(Token));
        var outerProvider = outer.BuildServiceProvider();

        var app = new SharedHostedWebApp(
            outerProvider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            outerProvider.GetRequiredService<TimeProvider>());
        // The REAL credential factory over the REAL Argon2id hasher: the recovery route now mints the
        // artifact node-side from the submitted password (#3366), so a stub factory would hide the
        // very step the contract change introduces.
        app.MapApiRoutes(routes =>
            RecoveryAcceptRoutes.Map(
                routes.MapPreAuthOperationalGroup(),
                new StubRecoveryAuthority(),
                new WebChosenCredentialFactory(
                    new Argon2idPasswordHasher<InstallationAccountRecord>(
                        Options.Create(new Argon2idHashOptions()))),
                new PassthroughAntiforgery(),
                new Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemRateLimiter(TimeProvider.System)));
        await app.StartAsync(CancellationToken.None);

        var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        return (app, client);
    }

    private static HttpRequestMessage PostRecovery(string code, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, RecoveryAcceptRoutes.RecoverPath)
        {
            Content = JsonContent.Create(new
            {
                code,
                password = "correct horse battery staple recovery",
            }),
        };
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        return req;
    }

    /// <summary>A valid code recovers; any other code refuses indistinguishably. "expired" maps to a
    /// DIFFERENT internal reason (AccountUnavailable) to prove it still yields the identical 401.</summary>
    private sealed class StubRecoveryAuthority : IAccountRecoveryAuthority
    {
        public Task<AccountRecoveryResult> RecoverAsync(
            AccountRecoveryCommand command,
            CancellationToken cancellationToken = default)
        {
            var status = command.RawCode switch
            {
                "good-recovery-code" => AccountRecoveryStatus.Recovered,
                "expired-recovery-code" => AccountRecoveryStatus.AccountUnavailable,
                _ => AccountRecoveryStatus.InvitationRefused,
            };
            return Task.FromResult(new AccountRecoveryResult(
                status,
                status == AccountRecoveryStatus.Recovered ? "acct-stub" : null,
                0));
        }
    }

    /// <summary>Passthrough antiforgery — consumes cleanly so the test exercises the caller-auth gate
    /// and the recovery authority, not antiforgery.</summary>
    private sealed class PassthroughAntiforgery : IWebAntiforgeryPolicy
    {
        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);
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
        public void EmitToken(HttpResponse response, string token) { }
        public void ExpireAnonymousBinding(HttpResponse response) { }
    }

    private static void AssertEndpoint(
        Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointSnapshot snapshot,
        string routePattern,
        string method,
        string callerAuthPolicy)
    {
        var endpoint = Assert.Single(
            snapshot.Endpoints,
            candidate => string.Equals(candidate.RoutePattern, routePattern, StringComparison.Ordinal));
        Assert.Contains(method, endpoint.HttpMethods);
        Assert.Equal(callerAuthPolicy, endpoint.ListenerCallerAuthPolicy);
    }
}
