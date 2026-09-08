using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Admission;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The admission family admits a founder selected session from the web plane and refuses every other
/// selected-session membership; the desktop plane remains unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>The deputy this closes.</b> <see cref="HostedAdmissionApiEndpoint"/> captures the admitter party id ONCE
/// at startup and replays it on every request; <see cref="AdmissionRoutes"/> consults no
/// <see cref="IAuthorizationContext"/> at all; and neither <c>/invites</c> nor <c>/redeem</c> is on the
/// listener's pre-auth allowlist. So a signed-in MEMBER minted and admitted AS THE GENESIS ADMITTER, signed
/// with this node's key — roster mutation under a borrowed identity, producing an artefact cryptographically
/// INDISTINGUISHABLE from the real admitter's. There is no after-the-fact audit that recovers it.
/// </para>
/// <para>
/// Reachable through shipped UI rather than only by hand: <c>EnrollmentSection</c>, which hosts the invite
/// panel, is mounted in <c>routes/comms.tsx</c>, and Comms is in the nav.
/// </para>
/// <para>
/// <b>Why these go through the REAL listener rather than the filter alone.</b>
/// <see cref="WebPlaneUnavailableRouteFence"/> documents that <c>AddEndpointFilter</c> registers a filter
/// FACTORY and not queryable <c>Endpoint.Metadata</c> — so nothing can ASK whether a family is fenced, and a
/// route mapped onto <c>app</c> instead of the group would be silently open with every filter-level test still
/// green. The only proof available today that the ADMISSION family is behind the fence is to drive a member
/// request at the real route over the real Kestrel listener and watch it be refused. The filter's own two
/// facts — refuse while bound, delegate otherwise — are pinned by
/// <c>FormsStartupCapturedIdentityFenceTests</c>; what is new here is the MAPPING.
/// </para>
/// </remarks>
public sealed class AdmissionWebPlaneFenceTests : IAsyncLifetime
{
    private const string CallerToken = "admission-web-plane-fence-caller-token-0001";
    private const string FounderHandle = "handle-founder-with-at-least-256-bits-of-admission-entropy-0";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-admission-entropy-00";
    private const string UnresolvedHandle = "handle-unresolved-with-at-least-256-bits-of-admission-entropy";
    private static readonly Guid Team = Guid.Parse("7e57bbbb-0000-0000-0000-00000000003a");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private readonly List<string> _dirs = new();
    private readonly List<IAsyncDisposable> _async = new();
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<SharedHostedWebApp> _apps = new();
    private readonly List<HttpClient> _clients = new();
    private readonly List<IDisposable> _disposables = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        foreach (var a in _apps) { await a.StopAsync(CancellationToken.None); await a.DisposeAsync(); }
        foreach (var d in _async) await d.DisposeAsync();
        foreach (var p in _providers) await p.DisposeAsync();
        foreach (var d in _disposables) d.Dispose();
        foreach (var dir in _dirs)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_signed_in_founder_can_mint_an_invite()
    {
        var started = await StartAsync();

        var response = await SendAsWebSessionAsync(started.Client, FounderHandle, NewInviteRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(
            string.IsNullOrWhiteSpace(body.RootElement.GetProperty("tokenId").GetString()),
            "the founder web session must mint a real invite token");
        Assert.Equal(1, await started.InviteRecordCountAsync());
    }

    [Fact]
    public async Task A_desktop_only_composition_resolves_without_a_web_identity_authority()
    {
        var started = await StartAsync(registerIdentityAuthority: false);

        var response = await SendAsDesktopOperatorAsync(started.Client, NewInviteRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await started.InviteRecordCountAsync());
    }

    [Fact]
    public async Task A_signed_in_member_cannot_mint_an_invite_as_the_genesis_admitter()
    {
        var started = await StartAsync();

        var response = await SendAsMemberAsync(started.Client, NewInviteRequest());

        // 403 and NOT 200: before the fence this returned a real invite token, minted against the admitter
        // party captured at startup and signed with the node key.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            AdmissionWebPlaneRouteFence.FounderOnlyCode,
            body.RootElement.GetProperty("code").GetString());

        // The wire carries a machine code, never English — and no invite material.
        Assert.False(body.RootElement.TryGetProperty("tokenId", out _));
        Assert.Equal(0, await started.InviteRecordCountAsync());
    }

    [Fact]
    public async Task An_unresolved_web_session_cannot_mint_an_invite()
    {
        var started = await StartAsync();

        var response = await SendAsWebSessionAsync(started.Client, UnresolvedHandle, NewInviteRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            AdmissionWebPlaneRouteFence.FounderOnlyCode,
            body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await started.InviteRecordCountAsync());
    }

    [Fact]
    public async Task A_signed_in_member_cannot_redeem_into_the_roster_either()
    {
        // /redeem is the mutating half — the one that lands a signed admission on the synced doctype. Pinned
        // separately from /invites because they are separate Map calls, and only a whole-family mapping covers
        // both. The body is deliberately junk: the fence must refuse BEFORE any of it is read.
        var started = await StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"{AdmissionRoutes.RouteBase}/redeem")
        {
            Content = JsonContent.Create(
                new { token = "not-a-real-token", partyId = "party-intruder", publicKey = "AAAA" }),
        };
        var response = await SendAsMemberAsync(started.Client, request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            AdmissionWebPlaneRouteFence.FounderOnlyCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_signed_in_member_cannot_trigger_a_join()
    {
        // POST /admission/join adopts a foreign roster, switches the active team and rebinds the gossip
        // daemon — the largest blast radius in the family after /redeem. It is mapped only when a join
        // service is wired, so it is exactly the route a harness that omits the optional services would
        // leave unpinned.
        var started = await StartAsync(withJoinRoute: true);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{AdmissionRoutes.RouteBase}/join")
        {
            Content = JsonContent.Create(new { admitterEndpoint = "http://127.0.0.1:1/", tokenId = "irrelevant" }),
        };
        var response = await SendAsMemberAsync(started.Client, request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            AdmissionWebPlaneRouteFence.FounderOnlyCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_desktop_operator_can_still_mint_an_invite()
    {
        // THE control. The fence is a restriction on WHERE the roster can be changed, not whether it can be —
        // a change that refused both planes would satisfy every assertion above and brick admission entirely.
        var started = await StartAsync();

        var response = await SendAsDesktopOperatorAsync(started.Client, NewInviteRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(
            string.IsNullOrWhiteSpace(body.RootElement.GetProperty("tokenId").GetString()),
            "the desktop plane must still mint a real invite token");
        Assert.Equal(1, await started.InviteRecordCountAsync());
    }

    private static HttpRequestMessage NewInviteRequest() =>
        new(HttpMethod.Post, $"{AdmissionRoutes.RouteBase}/invites")
        {
            Content = JsonContent.Create(new { ttlMinutes = (double?)null }),
        };

    /// <summary>Drives the request the way the Harborline App does — a selected-session cookie, no caller bearer.</summary>
    private static Task<HttpResponseMessage> SendAsMemberAsync(HttpClient client, HttpRequestMessage request)
        => SendAsWebSessionAsync(client, MemberHandle, request);

    private static Task<HttpResponseMessage> SendAsWebSessionAsync(
        HttpClient client,
        string selectedHandle,
        HttpRequestMessage request)
    {
        request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={selectedHandle}");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsDesktopOperatorAsync(
        HttpClient client, HttpRequestMessage request)
    {
        request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
        return client.SendAsync(request);
    }

    /// <summary>
    /// A real listener serving the REAL <see cref="HostedAdmissionApiEndpoint"/>, driven through its own
    /// <c>StartAsync</c> so the startup capture and the fence installation are the production ones. The genesis
    /// member is bound to the NODE signer, which is what <c>ResolveAndAssertAdmitter</c> requires.
    /// </summary>
    private async Task<StartedAdmissionApi> StartAsync(
        bool withJoinRoute = false,
        bool registerIdentityAuthority = true)
    {
        var seed = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(seed);
        var nodeSigner = new NodePrincipalSigner(seed);
        _disposables.Add(nodeSigner);

        const string founderParty = "party-genesis-admission-fence";
        var genesis = MemberRoster.Genesis(
            Team, founderParty, nodeSigner.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var roster = new NodeTeamRoster(genesis);

        var dir = Path.Combine(Path.GetTempPath(), $"harborline-admission-fence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        var inner = new ServiceCollection();
        inner.AddLogging(b => b.ClearProviders());
        inner.AddDbContextFactory<NodeLocalRosterDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "roster.db")};Pooling=False"));
        inner.AddDbContextFactory<NodeLocalAdmissionDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "admission.db")};Pooling=False"));
        inner.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        var sp = inner.BuildServiceProvider();
        _providers.Add(sp);
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        var admissionFactory = sp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
        await using (var ctx = await admissionFactory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RosterCrdtProjection>.Instance, roster);
        _async.Add(projection);

        var auditSigner = new Ed25519Signer(KeyPair.Generate());
        var auditTrail = new InMemoryAuditTrail();
        var sodAudit = new KernelAuditEnrollmentCompensatingControlRecorder(auditTrail, auditSigner, time: TimeProvider.System);

        var teamServices = new ServiceCollection().BuildServiceProvider();
        _providers.Add(teamServices);
        var activeTeam = new FixedActiveTeamAccessor(
            new TeamContext(new TeamId(Team), "Fence Team", teamServices, TimeProvider.System));

        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging(b => b.ClearProviders());
        outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
        outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
        outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());
        if (registerIdentityAuthority)
            outer.AddSingleton<IWebSelectedSessionIdentityAuthority>(new FixedSelectedSessionIdentityAuthority());
        var outerProvider = outer.BuildServiceProvider();
        _providers.Add(outerProvider);

        var app = new SharedHostedWebApp(
            outerProvider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            outerProvider.GetRequiredService<TimeProvider>());
        _apps.Add(app);

        // The REAL hosted endpoint. Its StartAsync performs the startup capture AND installs the fence in the
        // same call, so a change that separates them is visible here.
        var endpointServices = new ServiceCollection();
        endpointServices.AddSingleton(app);
        endpointServices.AddSingleton(
            new AdmissionCoordinator(Verifier, new DurableAdmissionTokenStore(admissionFactory), TimeProvider.System));
        endpointServices.AddSingleton(roster);
        endpointServices.AddSingleton(nodeSigner);
        endpointServices.AddSingleton(Verifier);
        endpointServices.AddSingleton(projection);
        endpointServices.AddSingleton(outerProvider.GetRequiredService<NodeCallerSessionToken>());
        endpointServices.AddSingleton<IEnrollmentCompensatingControlRecorder>(sodAudit);
        endpointServices.AddSingleton<IActiveTeamAccessor>(activeTeam);
        endpointServices.AddSingleton(
            outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HostedAdmissionApiEndpoint>>());
        if (registerIdentityAuthority)
        {
            endpointServices.AddSingleton(
                outerProvider.GetRequiredService<IWebSelectedSessionIdentityAuthority>());
        }
        if (withJoinRoute)
            endpointServices.AddSingleton(BuildJoinService(roster));
        var endpointProvider = endpointServices.BuildServiceProvider();
        _providers.Add(endpointProvider);
        var endpoint = ActivatorUtilities.CreateInstance<HostedAdmissionApiEndpoint>(endpointProvider);
        await endpoint.StartAsync(CancellationToken.None);

        await app.StartAsync(CancellationToken.None);
        var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        _clients.Add(client);
        return new StartedAdmissionApi(client, admissionFactory);
    }

    /// <summary>
    /// A real <see cref="NodeEnrollmentJoinService"/> over a stub transport — enough for the route to be
    /// MAPPED, which is all the fence assertion needs. The transport is never reached: the fence refuses
    /// before the handler runs, which is itself part of what the test proves.
    /// </summary>
    private NodeEnrollmentJoinService BuildJoinService(NodeTeamRoster roster)
    {
        var rootSeed = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
        var transportSigner = new Harborline.Api.Kernel.Security.Crypto.Ed25519Signer();
        var (rootPub, rootPriv) = transportSigner.GenerateFromSeed(rootSeed);
        var rootIdentity = new NodeIdentity(
            Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant(), rootPub, rootPriv);

        var client = new NodeWireEnrollmentClient(
            rootIdentity,
            new TeamSubkeyDerivation(transportSigner),
            new HkdfXWingSubkeyDerivation(new Harborline.Api.Kernel.Security.Crypto.XWingKem()),
            new Ed25519Signer(KeyPair.Generate()),
            "os:joiner-admission-fence",
            roster,
            Verifier, clock: TimeProvider.System);

        var factory = new TeamContextFactory(TimeProvider.System);
        _async.Add(factory);
        return new NodeEnrollmentJoinService(
            client, new RefusedEnrollmentTransport(), factory, new NoopStoreActivator(),
            new ActiveTeamAccessor(factory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeEnrollmentJoinService>.Instance);
    }

    private sealed class RefusedEnrollmentTransport : IEnrollmentTransport
    {
        public Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct) =>
            Task.FromResult<EnrollmentResponse?>(null);
    }

    private sealed class NoopStoreActivator : ITeamStoreActivator
    {
        public ValueTask ActivateAsync(TeamId teamId, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    /// <summary>
    /// Materializes ONE real <see cref="SelectedSessionRequestPrincipal"/> for the known handle — the only
    /// substituted seam, and it stands UPSTREAM of the route family under test. Its presence is what makes the
    /// listener open the attribution scope the fence reads.
    /// </summary>
    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal) ||
                string.Equals(selectedHandle, FounderHandle, StringComparison.Ordinal) ||
                string.Equals(selectedHandle, UnresolvedHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    accountId: "account-admission-fence",
                    tenantId: new TenantId(Team.ToString("D")),
                    principalUserId: new PrincipalUserId("principal-admission-fence"),
                    canonicalParty: new CanonicalPartyReference("party-member-admission-fence"),
                    membershipId: "membership-admission-fence",
                    membershipOwnerVersion: 2,
                    pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-admission-fence", 3)],
                    authorizationEpoch: 5,
                    sessionCorrelationId: "session-admission-fence",
                    coordinationCorrelationId: "coordination-admission-fence")
                : null);
    }

    private sealed class FixedSelectedSessionIdentityAuthority : IWebSelectedSessionIdentityAuthority
    {
        public Task<SelectedSessionIdentity?> DescribeAsync(
            string? selectedHandle,
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            var membership = selectedHandle switch
            {
                FounderHandle => SelectedSessionMembership.Founder,
                MemberHandle => SelectedSessionMembership.Member,
                _ => SelectedSessionMembership.Unresolved,
            };
            return Task.FromResult<SelectedSessionIdentity?>(new SelectedSessionIdentity(
                principal.AccountId,
                principal.CanonicalParty,
                DisplayName: null,
                principal.TenantId,
                TenantDisplayName: null,
                membership,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed record StartedAdmissionApi(
        HttpClient Client,
        IDbContextFactory<NodeLocalAdmissionDbContext> AdmissionFactory)
    {
        internal async Task<int> InviteRecordCountAsync()
        {
            await using var context = await AdmissionFactory.CreateDbContextAsync();
            return await context.AdmissionTokens.AsNoTracking().CountAsync();
        }
    }
}
