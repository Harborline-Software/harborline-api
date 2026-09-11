using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Authorization.DependencyInjection;
using Harborline.Api.Foundation.EngineRoom;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Shared Kestrel-backed route surface attached to the composition root's
/// <see cref="WebApplication"/> and service provider.
/// </summary>
/// <remarks>
/// <para>
/// The production composition creates one <see cref="WebApplication"/>, maps every route in an
/// explicit order, seals the executable endpoint registry, and then starts that same application.
/// Route handlers and middleware therefore resolve against the same provider as every non-HTTP
/// service. The provider-taking constructor remains for focused listener fixtures only.
/// </para>
/// <para>
/// <b>Lifecycle.</b>
/// <list type="number">
///   <item>Construction installs the listener middleware on the composition-root application.</item>
///   <item>The composition root maps routes through <see cref="MapApiRoutes"/>.</item>
///   <item><see cref="StartAsync"/> seals the executable endpoint registry before Kestrel binds.</item>
///   <item><see cref="CaptureSelectedUrl"/> records the address after the outer application starts.</item>
/// </list>
/// </para>
/// <para>
/// <b>Port selection.</b> Unchanged from Wave 5.2.D: <c>ASPNETCORE_URLS</c>
/// wins when present (Aspire / Bridge supervisor inject it); otherwise
/// <see cref="LocalNodeOptions.HealthPort"/> is honoured; <c>0</c> delegates
/// to Kestrel's ephemeral-port selection.
/// </para>
/// </remarks>
public sealed class SharedHostedWebApp : IHostedService, IAsyncDisposable
{
    // Test-harness only: set by ComposedHostBootSmokeTests on the child process it starts. No operator
    // or script surface, so the pre-rename spelling is not kept.
    private const string TestReadinessMarkerEnvironmentVariable =
        "HARBORLINE_TEST_READINESS_MARKER";

    private readonly WebApplication _app;
    private readonly ILogger<SharedHostedWebApp> _logger;
    private readonly LocalNodeOptions _options;
    private readonly string? _urlsOverride;
    private readonly LocalNodeExecutableEndpointRegistry _endpointRegistry;
    private readonly bool _callerAuthEnforced;
    private readonly bool _publicStaticFilesEnabled;
    private readonly X509Certificate2? _lanCertificate;
    private readonly LanConnectionRateLimiter? _lanRateLimiter;
    private readonly ILanDeviceSessionAuthority _lanDeviceSessionAuthority;
    private readonly bool _ownsApplication;
    private readonly object _endpointMappingGate = new();

    private EndpointLifecycleState _endpointState;
    private bool _healthMapped;

    /// <summary>
    /// inc-4 F1 — the EXPLICIT, REVIEWED allowlist of paths that bypass the
    /// listener-level caller-auth gate. Everything NOT in this set is gated
    /// fail-closed (401) when a session token is configured — so a route is
    /// authenticated UNLESS it is listed here. The list is deliberately tiny;
    /// adding to it is a security decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>/health</c>, <c>/live</c>, and <c>/ready</c></b> — aggregate, liveness, and
    /// readiness probes. The Bridge supervisor / Aspire probe these before
    /// the Harborline App connection (and therefore the bearer) exists; the probe does
    /// not hold the per-boot Harborline App token. The body carries only a coarse
    /// health status + a team GUID + a peer count — no financial/PII data — so a
    /// public operational probes are the standard, accepted exemption.
    /// </para>
    /// <para>
    /// <b><c>/ws</c></b> — the peer-to-peer sync transport (<see cref="MapWebSocketPath"/>).
    /// This is dialed by OTHER machines' sync daemons over LAN/Tailscale, which
    /// cannot present THIS node's per-boot Harborline App token. Peer authentication is
    /// enforced at a DIFFERENT layer — the sync daemon's shared-root trust gate
    /// (the HELLO handshake, #1261/#1263) — so gating <c>/ws</c> with the Harborline App
    /// token would BREAK cross-machine sync while adding no security (the peer
    /// trust gate already authenticates the remote). A deliberate, reviewed
    /// exemption with its own authenticated trust layer.
    /// </para>
    /// <para>
    /// <b><c>/api/session/login</c></b> — the WEB-CLIENT roster login (only reachable when
    /// <c>LocalNode:WebClient:Enabled</c>; the route 404s otherwise). A browser user cannot present a
    /// session before they HAVE one, so login must be pre-auth — exactly like <c>/health</c>.
    /// The handler itself verifies the password fail-closed and mints the expiring session (the
    /// HttpOnly session cookie + the body token); every OTHER <c>/api/session/*</c> route (logout,
    /// me) stays GATED (admitted only WITH a valid session cookie or bearer).
    /// </para>
    /// </remarks>
    /// <summary>
    /// URL Kestrel actually bound to — stable after <see cref="StartAsync"/>
    /// completes. <see langword="null"/> before start. Exposed primarily for
    /// in-process tests that need to know the auto-assigned port, and for
    /// <see cref="HostedHealthEndpoint"/>'s <c>SelectedUrl</c> passthrough.
    /// </summary>
    public string? SelectedUrl { get; private set; }

    /// <summary>
    /// The composition root service provider used by the application and all mapped routes.
    /// </summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The seal-once registry captured from the exact endpoint graph this app executes.</summary>
    internal LocalNodeExecutableEndpointRegistry ExecutableEndpointRegistry => _endpointRegistry;

    /// <summary>
    /// Construct the shared app. Builds the underlying <see cref="WebApplication"/>
    /// but does NOT start Kestrel — that happens in <see cref="StartAsync"/>.
    /// </summary>
    public SharedHostedWebApp(
        IServiceProvider outerServices,
        IOptions<LocalNodeOptions> options,
        LocalNodeExecutableEndpointRegistry endpointRegistry,
        ILogger<SharedHostedWebApp> logger,
        TimeProvider timeProvider,
        string? urlsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(outerServices);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpointRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options.Value;
        _endpointRegistry = endpointRegistry;
        _logger = logger;
        _urlsOverride = urlsOverride;
        _ownsApplication = true;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Suppress content-root inference: we share the outer host's cwd
            // but are not serving files from it.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Bridge the outer container's IActiveTeamAccessor into the inner
        // container so AddCheck<LocalNodeHealthCheck> can resolve it when
        // the health endpoint is mapped.
        builder.Services.AddSingleton(sp =>
            outerServices.GetRequiredService<Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor>());
        var desktopAuthorization = outerServices.GetService<Data.Financial.ActiveTeamAuthorizationContext>();
        if (desktopAuthorization is not null)
        {
            builder.Services.AddSingleton(desktopAuthorization);
        }
        // Ticket 205 slice 4 — bridge the OUTER container's authorization gate and clock into the inner
        // container. The record-scoped route families resolve each act at its point of use through
        // RequestAuthorization.RefusalAsync, which reads both from the request's own services; the inner
        // container cannot build the gate's durable readers itself (bug-2849), so the one composed
        // singleton crosses here rather than being threaded through nine Map(...) signatures. When the
        // outer host has no gate the guard refuses — an undecidable act does not happen.
        var authorizationGate =
            outerServices.GetService<Harborline.Api.Foundation.Authorization.AuthorizationGate>();
        if (authorizationGate is not null)
        {
            builder.Services.AddSingleton(authorizationGate);
        }
        // The HTTP read and feedback append share the shipping trail and signer, including on the
        // separate listener container used by the native runtime. Never create a second audit store.
        builder.Services.AddSingleton(_ => outerServices.GetRequiredService<Harborline.Api.Kernel.Audit.IAuditTrail>());
        builder.Services.AddSingleton(_ => outerServices.GetRequiredService<Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail>());
        builder.Services.AddSingleton(_ => outerServices.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationSigner>());
        if (outerServices.GetService<AuthorizationRefusalAudit>() is { } refusalAudit)
            builder.Services.AddSingleton(refusalAudit);
        builder.Services.AddScoped<Harborline.Api.Kernel.Audit.AuthorizationTraceReader>();
        var outerClock = outerServices.GetService<TimeProvider>();
        if (outerClock is not null)
        {
            builder.Services.AddSingleton(outerClock);
        }
        builder.Services.AddHarborlineEngineRoom();
        builder.Services.AddTransient(_ =>
            ActivatorUtilities.CreateInstance<LocalNodeHealthCheck>(outerServices));
        builder.Services.AddHealthChecks()
            .AddCheck<LocalNodeHealthCheck>("local-node")
            .AddCheck<LocalNodeLivenessCheck>("local-node-liveness", tags: ["live"])
            .AddCheck<LocalNodeReadinessCheck>("local-node-readiness", tags: ["ready"]);
        builder.Services.AddSingleton<ISelectedSessionPermissionResolver>(
            outerServices.GetService<ISelectedSessionPermissionResolver>()
            ?? new FailClosedSelectedSessionPermissionResolver());
        builder.Services.AddHarborlineTenantContext<WebSession.SelectedSessionTenantContext>();

        // inc-4 F1 — bridge the OUTER container's per-boot caller-auth token into
        // the inner WebApplication so the listener-level gate (below) can resolve
        // it. Built ONCE from LocalNode__SessionToken in Program.cs; the same
        // singleton instance the (now defence-in-depth) per-route checks use.
        var callerAuth = outerServices.GetRequiredService<NodeCallerSessionToken>();
        _callerAuthEnforced = callerAuth.IsEnforced;
        builder.Services.AddSingleton(callerAuth);

        if (_options.Lan.Enabled)
        {
            _lanCertificate = LocalNodeLanValidation.ValidateAndResolve(
                _options.Lan, _callerAuthEnforced, timeProvider,
                dataDirectory: _options.DataDirectory);
            _lanRateLimiter = new LanConnectionRateLimiter(timeProvider);
        }
        _lanDeviceSessionAuthority = outerServices.GetService<ILanDeviceSessionAuthority>()
            ?? new MissingLanDeviceSessionAuthority();

        // WEB-CLIENT profile (optional): the per-user session authority is an ADDITIONAL gate accept
        // path for a browser bearer, and the bundle root is served as static files. Both resolve to
        // null when LocalNode:WebClient:Enabled is false — the gate then behaves EXACTLY as before
        // (bootstrap token only) and no static files are served. Using GetService (not GetRequired-)
        // keeps the existing tests + the disabled-profile path working with no web registrations.
        var webAuthority = outerServices.GetService<WebSession.INodeWebSessionAuthority>();
        var selectedSessionAuthority =
            outerServices.GetService<IWebSelectedSessionPrincipalAuthority>();
        var webClientOptions = outerServices.GetService<IOptions<NodeWebClientOptions>>()?.Value;

        ConfigureUrls(builder.WebHost);
        ConfigureLanKestrel(builder.WebHost);

        _app = builder.Build();

        // Static hosting for the web-client bundle — registered BEFORE the caller-auth gate so the
        // login page + assets load WITHOUT a session (the API behind them stays gated). One origin,
        // no CORS surface. No-op unless the profile is enabled + the bundle dir is present.
        _publicStaticFilesEnabled = UseWebClientStaticFiles(_app, webClientOptions, _logger);

        _app.UseWebSockets();

        if (_options.Lan.Enabled)
        {
            UseLanListenerGate(_app, _lanDeviceSessionAuthority, _lanRateLimiter!);
        }

        // inc-4 F1 — LISTENER-LEVEL caller-auth: gate ALL routes by default. The web-session
        // authority (when present) is an ADDITIONAL accept path for a per-user browser bearer.
        UseListenerCallerAuth(
            _app,
            callerAuth,
            selectedSessionAuthority,
            webAuthority,
            _logger);

        // Ticket 066 Phase A: routing has selected the executable endpoint and caller admission has
        // published request-local attribution. An endpoint with neither explicit route-fence metadata
        // nor positive desktop attribution is refused before its handler.
        UnclassifiedRouteAudienceGuard.Use(_app, _logger);

        // ADR 0101 route-wide mutation contract: every POST/PUT/PATCH/DELETE under
        // /api/local-node honors Idempotency-Key, including newly added route families.
        // Install this after caller-auth so rejected callers cannot populate the replay store.
        NodeMutationIdempotency.UseOnce(_app, timeProvider);

        // Ticket 380 slice 1: a gate denial a handler met as an exception (a family that authorizes again
        // inside the service it calls) becomes the node's rendered, audited 403 here — never an empty 500.
        AuthorizationDenialTranslation.Use(_app);

        _app.MapSelectedSessionProductGroup()
            .MapPost("/membrane/invoke", (CapabilityRuntimeInvokeRequest _) => Results.Json(new { }))
            .WithCapabilityCorrelationTracing<CapabilityRuntimeInvokeRequest>(request => request.CorrelationId);
    }

    /// <summary>
    /// Attaches the listener pipeline to the composition root's already-built web application.
    /// The application and every mapped route therefore use one service provider.
    /// </summary>
    public SharedHostedWebApp(
        WebApplication app,
        IOptions<LocalNodeOptions> options,
        LocalNodeExecutableEndpointRegistry endpointRegistry,
        ILogger<SharedHostedWebApp> logger,
        TimeProvider timeProvider,
        X509Certificate2? lanCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpointRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _app = app;
        _options = options.Value;
        _endpointRegistry = endpointRegistry;
        _logger = logger;
        _urlsOverride = null;
        _ownsApplication = false;

        var services = app.Services;
        var callerAuth = services.GetRequiredService<NodeCallerSessionToken>();
        _callerAuthEnforced = callerAuth.IsEnforced;
        _lanDeviceSessionAuthority = services.GetService<ILanDeviceSessionAuthority>()
            ?? new MissingLanDeviceSessionAuthority();
        if (_options.Lan.Enabled)
        {
            _lanCertificate = lanCertificate ?? LocalNodeLanValidation.ValidateAndResolve(
                _options.Lan,
                _callerAuthEnforced,
                timeProvider,
                dataDirectory: _options.DataDirectory);
            _lanRateLimiter = new LanConnectionRateLimiter(timeProvider);
        }

        var webAuthority = services.GetService<WebSession.INodeWebSessionAuthority>();
        var selectedSessionAuthority =
            services.GetService<IWebSelectedSessionPrincipalAuthority>();
        var webClientOptions = services.GetService<IOptions<NodeWebClientOptions>>()?.Value;

        _publicStaticFilesEnabled = UseWebClientStaticFiles(_app, webClientOptions, _logger);
        _app.UseWebSockets();
        if (_options.Lan.Enabled)
        {
            UseLanListenerGate(_app, _lanDeviceSessionAuthority, _lanRateLimiter!);
        }
        UseListenerCallerAuth(
            _app,
            callerAuth,
            selectedSessionAuthority,
            webAuthority,
            _logger);
        UnclassifiedRouteAudienceGuard.Use(_app, _logger);
        NodeMutationIdempotency.UseOnce(_app, timeProvider);

        // Ticket 380 slice 1: a gate denial a handler met as an exception (a family that authorizes again
        // inside the service it calls) becomes the node's rendered, audited 403 here — never an empty 500.
        AuthorizationDenialTranslation.Use(_app);
        _app.MapSelectedSessionProductGroup()
            .MapPost("/membrane/invoke", (CapabilityRuntimeInvokeRequest _) => Results.Json(new { }))
            .WithCapabilityCorrelationTracing<CapabilityRuntimeInvokeRequest>(request => request.CorrelationId);
    }

    /// <summary>
    /// Serve the WEB-CLIENT bundle as static files at the node origin — the login page + JS/CSS load
    /// WITHOUT a session (they are not secrets; the API behind them stays gated), giving one origin +
    /// no CORS surface. Registered BEFORE the caller-auth gate so a bundle file short-circuits before
    /// the gate. No-op when the profile is disabled or the bundle directory is absent.
    /// </summary>
    private static bool UseWebClientStaticFiles(
        WebApplication app, NodeWebClientOptions? options, ILogger logger)
    {
        var bundleRoot = options?.BundleRoot;
        if (options is null || !options.Enabled || string.IsNullOrWhiteSpace(bundleRoot))
        {
            return false;
        }
        var fullPath = Path.GetFullPath(bundleRoot);
        if (!Directory.Exists(fullPath))
        {
            logger.LogWarning(
                "Web-client bundle root '{BundleRoot}' does not exist — static hosting NOT wired " +
                "(the node API is still reachable; there is just no UI in front of it).",
                fullPath);
            return false;
        }
        var provider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(fullPath);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
        UseWebClientShellFallback(app, provider);
        logger.LogInformation(
            "Web-client bundle served (static) from {BundleRoot} at the node origin (one origin, no CORS), "
            + "with the SPA shell fallback for client routes.",
            fullPath);
        return true;
    }

    /// <summary>
    /// Serve the SPA shell for a client-side route so a hard load, a reload or a bookmark reaches the
    /// client router instead of 404ing (card #3489).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was actually broken.</b> The Harborline App uses <c>createHashRouter</c>, so its own URLs carry
    /// the route in the fragment (<c>/#/team</c>) and a reload sends <c>GET /</c> — those always worked.
    /// What 404'd was the PATH form, <c>/team</c>, which is reachable three ways: hand-typed, an
    /// externally-authored link, and — the common one — cmd- or middle-clicking the app's own breadcrumbs,
    /// which are real anchors (<c>href: '/settings'</c>) whose left-click handler is bypassed by an
    /// open-in-new-tab. <c>public/hash-route-redirect.js</c> already spliced such a path into the hash once
    /// the shell loaded; the shell was simply never served, so the client half of that contract had no
    /// server half. Latent until the front door landed (#3329).
    /// </para>
    /// <para>
    /// <b>Position, and why the predicate is load-bearing.</b> This sits with the static middleware,
    /// BEFORE the caller-auth gate, so serving the shell short-circuits the gate exactly as serving a
    /// bundle file does — correct, because the shell is the login page and is not a secret. That
    /// position is also what makes the predicate the only thing standing between the API surface and an
    /// HTML body: a fallback that caught everything would turn an unauthenticated API call into a 200
    /// with HTML, which is worse than the 404 it replaced.
    /// </para>
    /// <para>
    /// <b>The predicate is an ALLOWLIST, deliberately.</b> The first version of this listed the prefixes
    /// the server owns and admitted everything else. That is the forgotten-route problem the caller-auth
    /// gate below exists to invert — map one endpoint outside those prefixes and this would swallow it
    /// AND its auth, with nothing failing. It is also porous: Kestrel does not decode <c>%2F</c> or
    /// collapse <c>//</c>, so <c>/api%2flocal-node%2fstatus</c>, <c>//api/…</c>, <c>/api\…</c> and
    /// <c>/api;/…</c> all slipped past a prefix test. None of them selected an endpoint, so nothing was
    /// ever reachable that should not have been — but each turned a logged 401 into a silent 200.
    /// </para>
    /// <para>
    /// So a request is a client route only if it looks like one. Every one of the Harborline App's route paths is
    /// slash-separated <c>[A-Za-z0-9-]</c> segments, so requiring exactly that admits all of them and
    /// rejects the whole encoded-evasion class at once — including, for free, any path bearing a file
    /// extension. Three further guards:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Anything routing owns is never swallowed.</b> If an endpoint was already selected, this
    /// delegates — the same check <c>StaticFileMiddleware</c> makes. Structural, so it needs no list.</item>
    /// <item><b>Reserved prefixes</b> (<c>/api</c>, <c>/ws</c>, <c>/health</c>, <c>/live</c>,
    /// <c>/ready</c>), matched on a segment
    /// boundary. Redundant with the endpoint check for mapped routes, but it also covers an UNMAPPED path
    /// under them — <c>/api/does-not-exist</c> must stay a real status, not the shell.</item>
    /// <item><b>GET and HEAD only.</b> A POST to an unknown path must not come back 200 with the shell —
    /// the same harm as swallowing <c>/api</c>, reached by a different door.</item>
    /// </list>
    /// <para>
    /// A missing asset therefore still declines here, and answers with whatever the pipeline says: 404 for
    /// an authenticated caller, 401 when caller-auth is enforced and the caller has no token. Either is
    /// correct; what matters is that it is never HTML, which would surface as a MIME-type failure in the
    /// browser and hide the real fault behind a confusing one.
    /// </para>
    /// <para>
    /// <b>Known shadow:</b> <c>health</c> is also a Harborline App client route, and the liveness probe wins. It
    /// is the one client route that cannot be deep-linked in path form. Pre-existing, and the precedence
    /// is the right way round.
    /// </para>
    /// </remarks>
    private static void UseWebClientShellFallback(
        WebApplication app, Microsoft.Extensions.FileProviders.IFileProvider provider)
    {
        app.Use(async (context, next) =>
        {
            // UseRouting is auto-inserted ahead of all user middleware, so an endpoint is already selected
            // for anything the server owns. Declining here means no future route can be swallowed by
            // omission from a hand-maintained list.
            if (context.GetEndpoint() is not null || !IsClientRouteRequest(context.Request))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var shell = provider.GetFileInfo("index.html");
            if (!shell.Exists)
            {
                // A bundle without an index.html is a broken build, not a client route. Fall through to
                // the real 404 rather than inventing a 200.
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            // The shell names hashed asset files, so caching it would pin a client to a stale bundle
            // across a deploy.
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(shell).ConfigureAwait(false);
        });
    }

    /// <summary>The guards described on <see cref="UseWebClientShellFallback"/>.</summary>
    private static bool IsClientRouteRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        foreach (var reserved in ServerReservedPathPrefixes)
        {
            if (path.Equals(reserved, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(reserved + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return EverySegmentLooksLikeAClientRoute(path);
    }

    /// <summary>
    /// True when every segment is a non-empty run of <c>[A-Za-z0-9-]</c> — the shape of every Harborline App route
    /// path. Rejects an empty segment (<c>//api/…</c>), a percent-escape Kestrel did not decode
    /// (<c>/api%2f…</c>), a path parameter (<c>/api;/…</c>), a backslash, and — because a dot is not in the
    /// set — anything bearing a file extension, so a missing asset declines to the real status.
    /// </summary>
    private static bool EverySegmentLooksLikeAClientRoute(string path)
    {
        // The bare origin is served by UseDefaultFiles upstream and never reaches here; treat it as a
        // client route anyway so the two cannot disagree.
        var rest = path.AsSpan(1);
        if (rest.IsEmpty)
        {
            return true;
        }

        // A single trailing slash is idiomatic ("/team/") and the router treats it as the same route.
        if (rest[^1] == '/')
        {
            rest = rest[..^1];
        }

        foreach (var range in rest.Split('/'))
        {
            var segment = rest[range];
            if (segment.IsEmpty)
            {
                return false;
            }

            foreach (var c in segment)
            {
                var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-';
                if (!ok)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Path prefixes the server owns. The endpoint check upstream already covers every MAPPED route; this
    /// list is what keeps an UNMAPPED path under them (<c>/api/does-not-exist</c>) from getting the shell.
    /// </summary>
    private static readonly string[] ServerReservedPathPrefixes = ["/api", "/ws", "/health", "/live", "/ready"];

    /// <summary>
    /// inc-4 F1 — install the LISTENER-LEVEL caller-auth middleware: a loopback
    /// bind authenticates the HOST, not the calling PROCESS, so EVERY route on
    /// this shared listener requires the Harborline App's per-boot session token by
    /// default. A request whose path is not allowlisted by
    /// <see cref="NodeListenerCallerAuthPolicy"/>
    /// is rejected fail-closed (401) when a token is configured; the few
    /// allowlisted paths (liveness, peer-sync — authenticated by their own
    /// layers) pass through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why gate-all-by-default (the structural fix for inc-4 F1).</b> The
    /// inc-4 (#1286) caller-auth was per-route OPT-IN — only 3 route groups
    /// called <see cref="NodeCallerSessionToken.Validate"/>, leaving every
    /// UNAUTHENTICATED financial-cluster write route (journal-entry, invoice,
    /// bill, payment, chart-of-accounts, accounting-period, entity, …) reachable
    /// by any local process WITHOUT the token. Per-route opt-in is the
    /// forgotten-route problem: a new route is insecure unless someone remembers
    /// to add the check. This middleware INVERTS the default — a route is gated
    /// unless EXPLICITLY allowlisted — so a newly-added route is secure by
    /// construction.
    /// </para>
    /// <para>
    /// <b>Runs first, before endpoint execution.</b> Registered immediately after
    /// <c>UseWebSockets</c> and before any <see cref="MapApiRoutes"/> mapping, so
    /// it short-circuits a rejected request before the route handler runs. The
    /// allowlist match is path-prefix (so <c>/ws</c> covers the upgrade and
    /// <c>/health</c> the probe) using an ordinal, case-insensitive compare.
    /// </para>
    /// <para>
    /// <b>Dev/single-host-trusted parity with #1286.</b> When NO token is
    /// configured (a direct <c>dotnet run</c> / a Bridge-spawned tenant child)
    /// <see cref="NodeCallerSessionToken.IsEnforced"/> is false and
    /// <see cref="NodeCallerSessionToken.Validate"/> allows every call — the
    /// shipped Harborline App ALWAYS injects a token, so the production path is always
    /// gated; the null path is the dev/test/Bridge fallback the node logs loudly
    /// at startup (Program.cs).
    /// </para>
    /// </remarks>
    internal static void UseListenerCallerAuth(
        IApplicationBuilder app,
        NodeCallerSessionToken callerAuth,
        IWebSelectedSessionPrincipalAuthority? selectedSessionAuthority,
        WebSession.INodeWebSessionAuthority? webAuthority,
        ILogger logger)
    {
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            // The LAN gate has already performed device-session admission. This marker is deliberately
            // distinct from the loopback bootstrap bearer and cannot be supplied by a caller.
            if (context.Features.Get<LanListenerRequestFeature>() is not null)
            {
                NodeCallerSessionToken.MarkGatePassed(context);
                await next(context).ConfigureAwait(false);
                return;
            }

            // Un-enforced (dev/single-host/Bridge): nothing to gate — fast path,
            // identical to the #1286 per-route un-enforced behaviour.
            if (!callerAuth.IsEnforced)
            {
                context.Features.Set(SingleHostTrustedRequestFeature.Instance);
                NodeCallerSessionToken.MarkGatePassed(context);
                await next(context).ConfigureAwait(false);
                return;
            }

            var path = context.Request.Path.Value ?? string.Empty;
            if (NodeListenerCallerAuthPolicy.IsAllowlisted(path))
            {
                NodeCallerSessionToken.MarkGatePassed(context);
                await next(context).ConfigureAwait(false);
                return;
            }

            // Accept 1 — the BOOTSTRAP per-boot token (the shipped Harborline App / API smoke). Fast
            // constant-time compare; tried first so the desktop path is unchanged.
            if (callerAuth.Validate(context.Request) is NodeCallerSessionToken.Decision.Allow)
            {
                context.Features.Set(BootstrapBearerRequestPrincipal.Instance);
                context.Features.Set(DesktopPlaneRequestFeature.Instance);
                NodeCallerSessionToken.MarkGatePassed(context);
                await next(context).ConfigureAwait(false);
                return;
            }

            var cookieDisposition = ClassifyWebCookieAudience(context.Request);

            // Accept 2 — the exact tenant-selected cookie audience. Presence is authoritative:
            // refusal never falls through to the legacy audience. Live revalidation constructs one
            // immutable principal, exposes that SAME instance as the request feature, and binds the
            // inner request-scoped Authorization.ITenantContext facade before downstream execution.
            if (selectedSessionAuthority is not null &&
                cookieDisposition == WebCookieAudienceDisposition.Selected)
            {
                var selectedHandle =
                    context.Request.Cookies[WebSession.WebSessionCookieNames.Selected];
                var principal = await selectedSessionAuthority.AuthenticateAsync(
                        selectedHandle,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                if (principal is not null)
                {
                    context.Features.Set(principal);
                    await context.RequestServices
                        .GetRequiredService<WebSession.SelectedSessionTenantContext>()
                        .BindAsync(principal, context.RequestAborted)
                        .ConfigureAwait(false);
                    NodeCallerSessionToken.MarkGatePassed(context);
                    // MTW-2 2612-C / card #3192 — publish the acting member's AUTHZ PROJECTION into the
                    // explicit ambient scope the audit enlister reads, for exactly this request. The
                    // principal itself stays request-feature-only (its own doctrine); only the flat
                    // snapshot travels. This scope transports attribution: without it the node's audit rows attribute
                    // the operator constant instead of the signed-in member. Deliberately NOT
                    // IHttpContextAccessor — the audit slice is composed on the OUTER generic host, whose
                    // container ASP.NET Core never populates from this inner serving app.
                    //
                    // ⚠ SECURITY — this scope is ALSO an AUTHORIZATION control (card #3356), not only an
                    // audit one. NodeCallerParty.Resolve and SelectedSessionTenantContext read it as THE
                    // signal for "a hosted-web member is acting", and refuse the desktop operator's
                    // identity and grants while it is open. So this Enter MUST cover EVERY selected-session
                    // request, READS INCLUDED, and must wrap the whole of next(context).
                    //
                    // The narrowing to refuse: "audit only records mutations, so only open the scope for
                    // writes / skip GET". That is reasonable audit hygiene and it silently UNFENCES every
                    // gated read route (GET /packs/installed, /packs/graph consumers, the channel feed).
                    // WebPlaneAuthorizationFenceTests drives a gated GET for exactly this reason — if you
                    // narrow this scope, that test goes red before the hole ships. Do not widen the
                    // condition to make it pass; the test is the requirement.
                    using var attributionScope = Data.Audit.NodeCallerAttributionScope.Enter(
                        Data.Audit.NodeCallerAttribution.From(principal));
                    try
                    {
                        await next(context).ConfigureAwait(false);
                    }
                    finally
                    {
                        context.Features.Set<SelectedSessionRequestPrincipal>(null);
                    }
                    return;
                }

                logger.LogWarning(
                    "selected-session request authority refused a stale or invalid handle for {Path} (401).",
                    path);
                await NodeCallerSessionToken.RejectResult().ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            // A selected cookie cannot become a legacy credential merely because the v2
            // principal authority is unavailable. Audience presence remains authoritative.
            if (cookieDisposition == WebCookieAudienceDisposition.Selected)
            {
                logger.LogWarning(
                    "selected-session request authority is unavailable for {Path} (401).",
                    path);
                await NodeCallerSessionToken.RejectResult().ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            // A purpose-bound web cookie is authoritative for its own audience even when the
            // selected-user cookie is absent. Challenge handles are consumed only by the
            // pre-auth selection route, and installation handles are confined to installation
            // commands. Neither may fall through to the legacy v1 cookie/bearer extractor on a
            // tenant business route. This branch is deliberately before Accept 3: otherwise a
            // request carrying a foreign-audience cookie plus a live legacy credential could
            // silently change audiences after the selected-user miss.
            if (cookieDisposition == WebCookieAudienceDisposition.Foreign)
            {
                logger.LogWarning(
                    "hosted-web request presented a cookie outside the business-route audience for " +
                    "{Path} (401).",
                    path);
                await NodeCallerSessionToken.RejectResult().ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            // Accept 3 — a per-user legacy WEB-CLIENT session for a browser user. Only when the web-client
            // profile is enabled; TryAuthenticateAsync reads the session id from EITHER the
            // HttpOnly+Secure session cookie (#131 — the refresh-surviving browser transport) OR an
            // Authorization: Bearer header (tooling), then validates + touches the session store
            // fail-closed. Extending THIS accept path (not the per-route checks) is what lets the
            // cookie session flow identically to the bearer. Stamp the gate-passed marker so a
            // per-route defence-in-depth caller-auth check downstream (SyncStatusRoutes / TeamRoutes
            // / CommsRoutes / CurrentPrincipalSignature — each knows only the bootstrap token)
            // accepts this already-authenticated web session instead of falsely 401-ing it and
            // bouncing the browser to the login screen (#1842).
            if (webAuthority is not null &&
                await webAuthority.TryAuthenticateAsync(context).ConfigureAwait(false))
            {
                context.Features.Set(DesktopPlaneRequestFeature.Instance);
                NodeCallerSessionToken.MarkGatePassed(context);
                await next(context).ConfigureAwait(false);
                return;
            }

            // Gate-all-by-default: neither the bootstrap token nor a live web session ⇒ fail-closed.
            // SECURITY: log the rejected PATH only — never the presented token (a token in a log is
            // a credential leak).
            logger.LogWarning(
                "inc-4 caller-auth: rejected unauthenticated loopback request to {Path} (401).",
                path);
            await NodeCallerSessionToken.RejectResult().ExecuteAsync(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// W4 listener gate. It is positive-allowlist by construction: only data descendants and the exact
    /// sessionless redeem route can reach a LAN handler. The loopback caller token and web cookie are
    /// never accepted as LAN credentials.
    /// </summary>
    internal static void UseLanListenerGate(
        IApplicationBuilder app,
        ILanDeviceSessionAuthority deviceSessionAuthority,
        LanConnectionRateLimiter rateLimiter)
    {
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (!IsLanRequest(context))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var source = NormalizeSource(context.Connection.RemoteIpAddress);
            if (!rateLimiter.TryAcquire(source, out var lease))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            using (lease)
            {
                if (context.Request.Headers.ContainsKey("Cookie"))
                {
                    await LanRouteUnavailable.RejectAsync(context).ConfigureAwait(false);
                    return;
                }

                if (LocalNodeLanRoutePolicy.IsPairingRedeem(context.Request))
                {
                    // PairingRedeemDispatch remains responsible for token proof, audience binding, CAS consume,
                    // and its existing source/tenant redeem limiter. This exception only crosses this gate.
                    context.Features.Set(LanListenerRequestFeature.Instance);
                    await next(context).ConfigureAwait(false);
                    return;
                }

                if (!LocalNodeLanRoutePolicy.IsDataRoute(context.Request.Path.Value ?? string.Empty))
                {
                    await LanRouteUnavailable.RejectAsync(context).ConfigureAwait(false);
                    return;
                }

                var authorization = context.Request.Headers.Authorization.ToString();
                if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(authorization[7..]))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                var principal = await deviceSessionAuthority.AuthenticateAsync(
                        context,
                        authorization[7..].Trim(),
                        context.RequestAborted)
                    .ConfigureAwait(false);
                if (principal is null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                // Successful device authentication is not an unauthenticated attempt. Release the
                // handshake lease and remove this request from the source's rolling unauthenticated
                // budget before the authenticated handler runs.
                LanConnectionRateLimiter.MarkAuthenticated(lease);
                context.Features.Set(principal);
                context.Features.Set(LanListenerRequestFeature.Instance);
                using var deviceScope = Data.Audit.NodeCallerAttributionScope.EnterDevice(
                    principal.DeviceId,
                    principal.TenantId,
                    principal.PrincipalId);
                await next(context).ConfigureAwait(false);
            }
        });
    }

    private static bool IsLanRequest(HttpContext context) =>
        context.Features.Get<LanListenerRequestFeature>() is not null;

    private static string NormalizeSource(IPAddress? address) =>
        address?.MapToIPv6().ToString() ?? "unknown";

    /// <summary>
    /// Request-local proof that listener admission used the installation bootstrap bearer. The
    /// permissions route consumes this marker to distinguish the desktop operator from legacy web
    /// credentials, neither of which carries a selected-session principal.
    /// </summary>
    internal sealed class BootstrapBearerRequestPrincipal
    {
        internal static BootstrapBearerRequestPrincipal Instance { get; } = new();

        private BootstrapBearerRequestPrincipal()
        {
        }
    }

    internal static WebCookieAudienceDisposition ClassifyWebCookieAudience(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Cookies.ContainsKey(WebSession.WebSessionCookieNames.Selected))
        {
            return WebCookieAudienceDisposition.Selected;
        }
        if (request.Cookies.ContainsKey(WebSession.WebSessionCookieNames.Challenge) ||
            request.Cookies.ContainsKey(WebSession.WebSessionCookieNames.Installation))
        {
            return WebCookieAudienceDisposition.Foreign;
        }
        return WebCookieAudienceDisposition.LegacyEligible;
    }

    /// <summary>
    /// Register the aggregate, liveness, and readiness mappings if no sibling has done so already.
    /// Idempotent. Called by <see cref="HostedHealthEndpoint.StartAsync"/>.
    /// Must be called before <see cref="StartAsync"/>.
    /// </summary>
    public void MapHealthCheckIfAbsent()
    {
        lock (_endpointMappingGate)
        {
            ObjectDisposedException.ThrowIf(_endpointState is not EndpointLifecycleState.Mapping, this);
            if (_healthMapped)
            {
                return;
            }
            _app.MapLocalNodeHealthProbes();
            _healthMapped = true;
        }
    }

    /// <summary>
    /// Registers additional HTTP API routes on the shared app using a configuration
    /// callback. Multiple callers may call this method; each receives the same
    /// <see cref="WebApplication"/> instance. Must be called before
    /// <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="configure">
    /// Callback that maps routes via <c>app.MapGet</c>, <c>app.MapPost</c>, etc.
    /// </param>
    public void MapApiRoutes(Action<WebApplication> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        lock (_endpointMappingGate)
        {
            ObjectDisposedException.ThrowIf(_endpointState is not EndpointLifecycleState.Mapping, this);
            configure(_app);
        }
    }

    /// <summary>
    /// Register a WebSocket upgrade handler for <paramref name="path"/>. The
    /// <paramref name="onAccepted"/> callback is invoked once the upgrade
    /// completes, receiving the raw <see cref="WebSocket"/> handle. The HTTP
    /// request lifetime is tied to the callback's completion — return from
    /// <paramref name="onAccepted"/> only after the WebSocket session is done.
    /// </summary>
    /// <remarks>
    /// Non-WebSocket requests to <paramref name="path"/> respond with
    /// HTTP 400. Must be called before <see cref="StartAsync"/>.
    /// </remarks>
    public void MapWebSocketPath(string path, Func<WebSocket, CancellationToken, Task> onAccepted)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(onAccepted);
        lock (_endpointMappingGate)
        {
            ObjectDisposedException.ThrowIf(_endpointState is not EndpointLifecycleState.Mapping, this);
            var operational = _app.MapPreAuthOperationalGroup();
            operational.Map(path, async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await onAccepted(ws, ctx.RequestAborted).ConfigureAwait(false);
            });
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_endpointMappingGate)
        {
            if (_endpointState is EndpointLifecycleState.Started)
            {
                return;
            }
            if (_endpointState is not EndpointLifecycleState.Mapping)
            {
                throw new InvalidOperationException(
                    $"The shared endpoint host cannot start from state '{_endpointState}'.");
            }

            _endpointState = EndpointLifecycleState.Sealing;
            try
            {
                _endpointRegistry.Seal(
                    ((IEndpointRouteBuilder)_app).DataSources,
                    _callerAuthEnforced,
                    _publicStaticFilesEnabled);
                if (_options.Lan.Enabled && !_endpointRegistry.Current.ListenerCallerAuthEnforced)
                {
                    throw new InvalidOperationException(
                        "The sealed LAN endpoint snapshot does not enforce listener caller authentication.");
                }
                _endpointState = EndpointLifecycleState.Sealed;
            }
            catch (RouteFenceViolationException)
            {
                // An unrepresentable endpoint shape may degrade because the registry is technical
                // evidence, not request authority. An unfenced consequential route may not: binding
                // the unchanged app would serve it without its security boundary, so refuse startup.
                _endpointState = EndpointLifecycleState.Failed;
                throw;
            }
            catch (Exception exception) when (!_options.Lan.Enabled)
            {
                // The registry is technical inventory evidence, not request authority. An existing
                // installation must remain startable if a newly introduced endpoint shape cannot yet
                // be represented. Preserve the fail-closed evidence posture (the registry stays
                // unsealed), report the degradation loudly, and continue binding the unchanged app.
                _endpointState = EndpointLifecycleState.SealDegraded;
                _logger.LogError(
                    exception,
                    "Local-node executable endpoint inventory could not be sealed; " +
                    "Kestrel startup will continue with registry evidence unavailable.");
            }
            catch
            {
                _endpointState = EndpointLifecycleState.Failed;
                throw;
            }
        }

        try
        {
            if (_ownsApplication)
            {
                await _app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            lock (_endpointMappingGate)
            {
                _endpointState = _ownsApplication
                    ? EndpointLifecycleState.Started
                    : EndpointLifecycleState.Sealed;
            }
        }
        catch
        {
            lock (_endpointMappingGate)
            {
                _endpointState = EndpointLifecycleState.Failed;
            }
            throw;
        }

        if (_ownsApplication)
        {
            CaptureSelectedUrl();
        }
    }

    internal void CaptureSelectedUrl()
    {
        var serverAddresses = _app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        SelectedUrl = serverAddresses?.Addresses?.FirstOrDefault();

        _logger.LogInformation(
            "Wave 5.3.C shared-hosted-web-app bound at {Url} (LocalNode:HealthPort={HealthPort}); " +
            "health-mapped={HealthMapped}",
            SelectedUrl ?? "(unknown)",
            _options.HealthPort,
            _healthMapped);

        // The composed-host smoke test opts into a process-local readiness handshake. This is
        // emitted only after Kestrel has actually bound and SelectedUrl contains the assigned
        // ephemeral port; it avoids making another test release a port and hope the child wins
        // the subsequent bind race.
        var readinessMarker = Environment.GetEnvironmentVariable(
            TestReadinessMarkerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(readinessMarker) &&
            !string.IsNullOrWhiteSpace(SelectedUrl))
        {
            Console.WriteLine(readinessMarker + SelectedUrl);
        }

        lock (_endpointMappingGate)
        {
            if (_endpointState is EndpointLifecycleState.Sealed)
            {
                _endpointState = EndpointLifecycleState.Started;
            }
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_endpointMappingGate)
        {
            if (_endpointState is not EndpointLifecycleState.Started)
            {
                return;
            }
        }

        try
        {
            if (_ownsApplication)
            {
                await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the outer host's shutdown token fires before
            // Kestrel's graceful-stop completes. Not an error condition.
        }
    }

    private enum EndpointLifecycleState
    {
        Mapping,
        Sealing,
        Sealed,
        SealDegraded,
        Started,
        Failed,
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsApplication)
        {
            await ((IAsyncDisposable)_app).DisposeAsync().ConfigureAwait(false);
        }
        _lanCertificate?.Dispose();
    }

    internal static X509Certificate2? ConfigureHosting(
        IWebHostBuilder webHost,
        LocalNodeOptions options,
        bool callerAuthEnforced,
        TimeProvider timeProvider,
        string? urlsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(options);

        var envUrls = urlsOverride ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(envUrls))
        {
            if (options.Lan.Enabled)
            {
                LocalNodeLanValidation.ValidateEnvironmentUrls(envUrls);
            }
            if (urlsOverride is not null)
            {
                webHost.UseUrls(urlsOverride);
            }
        }
        else
        {
            webHost.UseUrls($"http://127.0.0.1:{options.HealthPort}");
        }

        if (!options.Lan.Enabled)
        {
            return null;
        }

        var certificate = LocalNodeLanValidation.ValidateAndResolve(
            options.Lan,
            callerAuthEnforced,
            timeProvider,
            dataDirectory: options.DataDirectory);
        var bindAddress = IPAddress.Parse(options.Lan.BindAddress!);
        var loopbackPort = string.IsNullOrWhiteSpace(envUrls)
            ? options.HealthPort
            : envUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => new Uri(value, UriKind.Absolute).Port)
                .Distinct()
                .Single();
        webHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(IPAddress.Loopback, loopbackPort);
            serverOptions.Listen(bindAddress, options.Lan.Port, listenOptions =>
            {
                listenOptions.Use(next => connection =>
                {
                    connection.Features.Set(LanListenerRequestFeature.Instance);
                    return next(connection);
                });
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = certificate;
                });
            });
        });
        return certificate;
    }

    private void ConfigureUrls(IWebHostBuilder webHost)
    {
        // ASPNETCORE_URLS wins when present — Aspire and the Bridge supervisor
        // inject it. Otherwise honour LocalNode:HealthPort; if it is 0 (the
        // default), Kestrel picks an ephemeral port.
        var envUrls = _urlsOverride ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(envUrls))
        {
            if (_options.Lan.Enabled)
            {
                LocalNodeLanValidation.ValidateEnvironmentUrls(envUrls);
            }
            if (_urlsOverride is not null)
            {
                webHost.UseUrls(_urlsOverride);
            }
            // Respect env-injected URLs — do not override.
            return;
        }

        var port = _options.HealthPort;
        webHost.UseUrls($"http://127.0.0.1:{port}");
    }

    private void ConfigureLanKestrel(IWebHostBuilder webHost)
    {
        if (!_options.Lan.Enabled)
        {
            return;
        }

        var bindAddress = IPAddress.Parse(_options.Lan.BindAddress!);
        var loopbackPort = ResolveLoopbackPort();
        webHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(IPAddress.Loopback, loopbackPort);
            serverOptions.Listen(bindAddress, _options.Lan.Port, listenOptions =>
            {
                // This is a connection-level marker, not an address comparison. It survives into
                // HttpContext.Features for every request accepted by this listener.
                listenOptions.Use(next => connection =>
                {
                    connection.Features.Set(LanListenerRequestFeature.Instance);
                    return next(connection);
                });
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = _lanCertificate;
                    httpsOptions.OnAuthenticate = (_, sslOptions) =>
                    {
                        sslOptions.ServerCertificate = _lanCertificate;
                    };
                });
            });
        });
    }

    private int ResolveLoopbackPort()
    {
        var envUrls = _urlsOverride ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (string.IsNullOrWhiteSpace(envUrls))
        {
            return _options.HealthPort;
        }

        var ports = envUrls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => new Uri(value, UriKind.Absolute).Port)
            .Distinct()
            .ToArray();
        if (ports.Length != 1)
        {
            throw new InvalidOperationException(
                "ASPNETCORE_URLS must contain exactly one loopback HTTP port when the W4 LAN listener is enabled.");
        }

        return ports[0];
    }
}

internal sealed record CapabilityRuntimeInvokeRequest
{
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
}

internal enum WebCookieAudienceDisposition
{
    Selected,
    Foreign,
    LegacyEligible,
}
