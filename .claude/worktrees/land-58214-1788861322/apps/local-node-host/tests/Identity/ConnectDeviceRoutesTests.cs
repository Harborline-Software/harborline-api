using System;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MTW-2 #3167 (adversarial verdict M1) — the connect-device mint route must NOT let an authenticated session mint
/// a years-long single-use bearer token. <see cref="ConnectDeviceRoutes.ClampMintTtl"/> bounds the caller-supplied
/// TTL to a short ceiling (absent / non-positive / non-finite → the short default; over-ceiling → the ceiling,
/// minted not refused), so <c>AdmissionToken.Mint</c> — which enforces only <c>&gt; 0</c> — can never receive an
/// attacker-chosen span. M2 additionally exercises the mapped endpoint over the real listener and proves a durable
/// binding fault collapses to the route's opaque refusal instead of escaping as a 500.
/// </summary>
public sealed class ConnectDeviceRoutesTests
{
    [Theory(DisplayName = "connect-device: caller TTL is clamped to [default, max] — no unbounded bearer lifetime (M1)")]
    [InlineData(null, 15.0)]      // absent → short default
    [InlineData(5.0, 5.0)]        // in-range → honored
    [InlineData(60.0, 60.0)]      // exactly the ceiling → honored
    [InlineData(600.0, 60.0)]     // over-ceiling (years) → CLAMPED to the ceiling (mints, does not refuse)
    [InlineData(0.0, 15.0)]       // non-positive → default
    [InlineData(-30.0, 15.0)]     // negative → default
    public void ClampMintTtl_Bounds_The_Caller_Supplied_Lifetime(double? requestedMinutes, double expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), ConnectDeviceRoutes.ClampMintTtl(requestedMinutes));
    }

    [Theory(DisplayName = "connect-device: a non-finite TTL (NaN / ±infinity) falls back to the default, never TimeSpan.FromMinutes(NaN)")]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampMintTtl_Rejects_NonFinite(double requestedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(ConnectDeviceRoutes.DefaultTtlMinutes),
            ConnectDeviceRoutes.ClampMintTtl(requestedMinutes));
    }

    [Fact(DisplayName = "connect-device: the real HTTP route mints a bounded token for an authenticated session")]
    public async Task Real_Http_Route_Mints_For_The_Authenticated_Principal()
    {
        await using var h = await RouteHarness.StartAsync(new InMemoryWebPairingInviteBindingStore());

        var response = await h.PostAsync(new { ttlMinutes = 600.0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"tokenId\":", body, StringComparison.Ordinal);
        Assert.Contains("\"joiningPartyId\":\"party-1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"expiresAt\":", body, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact(DisplayName = "connect-device: a durable Bind fault through the real HTTP route is an opaque refusal, never 500 (M2)")]
    public async Task Real_Http_Route_Contains_Binding_Store_Fault()
    {
        await using var h = await RouteHarness.StartAsync(new ThrowingBindingStore());

        var response = await h.PostAsync(new { ttlMinutes = double.MaxValue });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "{\"error\":\"connect_device_failed\",\"message\":\"Device pairing could not be started.\"}",
            await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        // earlier repository ticket #3311 — this refusal spent the single-use token, so it must hand back a fresh one.
        // Nothing else in the pipeline stamps this header, so its presence here is the re-issue and
        // nothing else: deleting that one line turns this red.
        Assert.True(
            response.Headers.TryGetValues(WebAntiforgeryPolicy.HeaderName, out var replacement),
            "the refusal carried NO antiforgery header");
        Assert.Equal("replacement-token", replacement!.Single());
    }

    private sealed class ThrowingBindingStore : IWebPairingInviteBindingStore
    {
        public void Bind(WebPairingInviteBinding binding) =>
            throw new InvalidOperationException("induced pairing binding fault");

        public WebPairingInviteBinding? Lookup(string tokenId) => null;
    }

    private sealed class RouteHarness : IAsyncDisposable
    {
        private const string BootstrapToken = "connect-device-test-bootstrap-token";
        private const string SelectedHandle = "selected-route-test";

        private readonly ServiceProvider _outer;
        private readonly SharedHostedWebApp _app;
        private readonly HttpClient _client;

        private RouteHarness(ServiceProvider outer, SharedHostedWebApp app, HttpClient client)
        {
            _outer = outer;
            _app = app;
            _client = client;
        }

        internal static async Task<RouteHarness> StartAsync(IWebPairingInviteBindingStore bindings)
        {
            var key = KeyPair.Generate();
            var signer = new Ed25519Signer(key);
            var verifier = new Ed25519Verifier();
            var team = Guid.Parse("7e57aaaa-0000-0000-0000-00000000000c");
            var roster = new NodeTeamRoster(MemberRoster.Genesis(
                team, "founder", signer, verifier, DateTimeOffset.UtcNow, Guid.NewGuid()));
            var mint = new WebAdmittedMemberPairingTokenMint(
                new AdmissionCoordinator(verifier, new InMemoryAdmissionTokenStore(), clock: TimeProvider.System),
                bindings);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddTestKernelClock();
            services.AddSingleton<IActiveTeamAccessor>(new NoActiveTeamAccessor());
            services.AddSingleton(new NodeCallerSessionToken(BootstrapToken));
            var outer = services.BuildServiceProvider();
            var app = new SharedHostedWebApp(
                outer,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
                outer.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
                outer.GetRequiredService<TimeProvider>());
            app.MapApiRoutes(routes =>
            {
                routes.Use(async (context, next) =>
                {
                    context.Features.Set(Principal());
                    await next(context);
                });
                ConnectDeviceRoutes.Map(
                    routes.MapSelectedSessionProductGroup(),
                    mint,
                    roster,
                    new AcceptingAntiforgeryPolicy());
            });
            await app.StartAsync(CancellationToken.None);
            var client = new HttpClient
            {
                BaseAddress = new Uri(app.SelectedUrl!),
                Timeout = TimeSpan.FromSeconds(10),
            };
            return new RouteHarness(outer, app, client);
        }

        internal Task<HttpResponseMessage> PostAsync(object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ConnectDeviceRoutes.ConnectPath)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BootstrapToken);
            request.Headers.Add("Cookie", $"__Host-hl-selected={SelectedHandle}");
            return _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outer.DisposeAsync();
        }

        private static SelectedSessionRequestPrincipal Principal() =>
            new(
                "account-1",
                new TenantId("tenant-1"),
                new PrincipalUserId("principal-1"),
                new CanonicalPartyReference("party-1"),
                "membership-1",
                3,
                [new PinnedGrantOwnerVersion("grant-1", 4)],
                7,
                "session-1",
                "coordination-1");
    }

    private sealed class NoActiveTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class AcceptingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) => Task.FromResult(true);
        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) => Task.FromResult(true);
        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);
        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) => Task.FromResult(true);
        // Declared on IWebAntiforgeryPolicy before this branch's merge base, and already called by
        // ConnectDeviceRoutes. Omitting it was a CS0535 that failed the WHOLE test project, so none of
        // this branch's new tests had ever run.
        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle)
        {
            // The REAL policy emits the header from inside RotateAudienceAsync. A double that returns
            // true without emitting lets a test pass on a response the server never sends (ADR 0130
            // anti-pattern A1) — and the header is the point: it is what the browser sends next.
            EmitToken(context.Response, "replacement-token");
            return Task.FromResult(true);
        }
        public void EmitToken(HttpResponse response, string token) =>
            response.Headers[WebAntiforgeryPolicy.HeaderName] = token;
        public void ExpireAnonymousBinding(HttpResponse response) { }
    }
}
