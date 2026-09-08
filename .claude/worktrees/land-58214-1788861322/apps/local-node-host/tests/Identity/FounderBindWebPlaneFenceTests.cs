using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// <c>POST /api/session/founder-bind</c> is DESKTOP-PLANE ONLY (card earlier repository ticket #3490, CIC ruling
/// 2026-08-01) — and, as an interim posture, that leaves it reachable by NO caller. Both halves are
/// asserted here, at the route, because the second is the half a filter-level test cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <b>The exposure this closes.</b> The route is the only ROUTE that can write
/// <c>InstallationIdentityRootDesignationRecord</c> — the singleton naming the installation's root — and
/// it binds the CALLER's own account. <c>WebFounderBindAuthority</c> states it "adds no policy of its
/// own", and the handler checks only that some selected-session principal exists. So on an installation
/// with no designation, the FIRST signed-in account to post here became the root, and
/// <c>InstallationFounderBindingService</c> then refuses every later attempt as
/// <c>AlreadyDesignated</c> — with no repair path anywhere in the tree.
/// </para>
/// <para>
/// Every installation that bootstrapped before #3467 is in that state. Customer-zero has two accounts
/// and no designation, so its joiner could have taken the founder's place, irreversibly.
/// </para>
/// <para>
/// <b>Why this is an interim closure and not the fix for #3490.</b> The handler requires a published
/// <see cref="SelectedSessionRequestPrincipal"/>, and the fence refuses whenever
/// <c>NodeCallerAttributionScope.HasBoundWebPrincipal</c> is set. Those are the SAME condition:
/// <c>SharedHostedWebApp</c> publishes the feature and opens the scope on adjacent lines of one
/// <c>if (principal is not null)</c> block, and both are the only production sites of either. Their
/// intersection is empty, so after the fence nobody can designate. The desktop plane's 401 is NOT caused
/// by the fence — it is identical before it, because the desktop plane carries a bootstrap token that
/// authenticates the HOST and never a principal that names an account.
/// </para>
/// <para>
/// That is a deliberate trade and it is the fail-closed one: an irreversible wrong designation cannot be
/// undone, whereas an inability to designate is recoverable by the follow-up that gives the desktop plane
/// an evidence path. #3490 stays open and #3386 stays blocked on it.
/// </para>
/// </remarks>
public sealed class FounderBindWebPlaneFenceTests : IAsyncLifetime
{
    private const string BootstrapToken = "founder-bind-fence-bootstrap-token-0001";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-founder-entropy-0000";

    private ServiceProvider? _provider;
    private SharedHostedWebApp? _app;
    private HttpClient? _client;
    private readonly RecordingBindAuthority _authority = new();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IActiveTeamAccessor>(NodeTestActiveTeam.Accessor);
        services.AddSingleton(new NodeCallerSessionToken(BootstrapToken));
        services.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());
        _provider = services.BuildServiceProvider();

        _app = new SharedHostedWebApp(
            _provider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            _provider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            _provider.GetRequiredService<TimeProvider>());

        // The REAL Map, so the fence it installs is the production one.
        _app.MapApiRoutes(app => FounderBindRoutes.Map(app, _authority, new AcceptingAntiforgery()));

        await _app.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri(_app.SelectedUrl!) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) { await _app.StopAsync(CancellationToken.None); await _app.DisposeAsync(); }
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task A_signed_in_member_cannot_claim_the_installation_root()
    {
        // THE closure. Before the fence this returned 200 and designated the caller's own account.
        var request = NewBindRequest();
        request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");

        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());

        // The authority is the singleton writer. It must not have been reached at all.
        Assert.False(_authority.Reached);
    }

    [Fact]
    public async Task No_caller_can_designate_today_and_that_is_the_interim_posture()
    {
        // The honest half, and the one a filter-level test structurally cannot see. A control asserting
        // "the desktop operator can still designate" would have passed here while being false, because
        // the filter delegates on the desktop plane and the HANDLER then refuses for want of a principal.
        //
        // If this ever starts failing because a desktop caller succeeded, that is the #3490 follow-up
        // landing — update this test rather than deleting it, and reopen the question it documents.
        var desktop = NewBindRequest();
        desktop.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + BootstrapToken);

        var response = await _client!.SendAsync(desktop);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(_authority.Reached);

        // The exact refusal branch, not merely "not the fence": the handler declining for want of a
        // principal. Pinned so a handler that starts refusing for some other reason is visible.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("bind_failed", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Presenting_the_bootstrap_token_wins_the_accept_but_still_reaches_nothing()
    {
        // Measured, and NOT what I first assumed. A caller holding BOTH the bootstrap token and a
        // selected cookie is admitted by the listener's token accept, which runs first and returns
        // before the selected-session branch — so no principal is published, no attribution scope is
        // opened, and the fence sees a DESKTOP caller and delegates. The handler then refuses anyway,
        // for want of the principal it requires.
        //
        // Worth pinning precisely because it looks like an escape hatch and is not: the token buys past
        // the fence and still designates nothing. It is the same 401 as the token-only case, which is
        // what makes "no caller can designate" true rather than merely likely.
        var request = NewBindRequest();
        request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + BootstrapToken);
        request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");

        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(_authority.Reached);

        // Not the fence — the token accept short-circuited before the scope could open.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("error").GetString());
    }

    private static HttpRequestMessage NewBindRequest() =>
        new(HttpMethod.Post, FounderBindRoutes.BindPath)
        {
            Content = JsonContent.Create(new { idempotencyKey = "idem-founder-bind-fence" }),
        };

    /// <summary>Records whether the singleton writer was reached at all. It must never be, on any plane.</summary>
    private sealed class RecordingBindAuthority : IWebFounderBindAuthority
    {
        internal bool Reached { get; private set; }

        public Task<WebFounderBindOutcome> BindAsync(
            WebFounderBindRequest request, CancellationToken cancellationToken = default)
        {
            Reached = true;
            return Task.FromResult(new WebFounderBindOutcome(
                WebFounderBindStatus.Bound, request.AccountId, 1, DateTimeOffset.UnixEpoch));
        }
    }

    /// <summary>Accepts every token, so a refusal here can only be the fence or the handler.</summary>
    private sealed class AcceptingAntiforgery : IWebAntiforgeryPolicy
    {
        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeChallengeAsync(HttpContext context, string h) => Task.FromResult(true);
        public Task<bool> ConsumeSelectedAsync(HttpContext context, string h) => Task.FromResult(true);
        public Task<bool> ConsumeInstallationAsync(HttpContext context, string h) => Task.FromResult(true);
        public Task<bool> RotateChallengeAsync(HttpContext context, string h) => Task.FromResult(true);
        public Task<bool> RotateSelectedAsync(HttpContext context, string h) => Task.FromResult(true);
        public void EmitToken(HttpResponse response, string token) { }
        public void ExpireAnonymousBinding(HttpResponse response) { }
    }

    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    accountId: "account-founder-bind-fence",
                    tenantId: new TenantId(NodeTestActiveTeam.TestTeamId.Value.ToString("D")),
                    principalUserId: new PrincipalUserId("principal-founder-bind-fence"),
                    canonicalParty: new CanonicalPartyReference("party-member-founder-bind-fence"),
                    membershipId: "membership-founder-bind-fence",
                    membershipOwnerVersion: 2,
                    pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-founder-bind-fence", 3)],
                    authorizationEpoch: 5,
                    sessionCorrelationId: "session-founder-bind-fence",
                    coordinationCorrelationId: "coordination-founder-bind-fence")
                : null);
    }
}
