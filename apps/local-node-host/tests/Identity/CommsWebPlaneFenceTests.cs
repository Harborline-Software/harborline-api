using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The comms family is DESKTOP-PLANE ONLY (card earlier repository ticket #3383).
/// </summary>
/// <remarks>
/// <para>
/// <b>The deputy this closes.</b> <see cref="HostedCommsApiEndpoint"/> resolves the active member party ONCE
/// at startup and stamps it as the AUTHOR of every appended message. The per-route
/// <c>callerAuth.Validate</c> reads like the guard that would stop a web caller and is not one — it returns
/// <c>Allow</c> on the listener's gate-passed marker, which every selected-session request carries. So a
/// signed-in MEMBER's message was authored as the OPERATOR's roster party, signed with the node key, and
/// merged into a CRDT that peers accept.
/// </para>
/// <para>
/// The propagation is the harm, and it is why this is fenced rather than logged: a CRDT merge has no notion
/// of retracting a provenance claim, so the wrong author becomes a signed fact on every peer that syncs.
/// </para>
/// <para>
/// <b>These assert on the DURABLE RECORD, not the status code</b> (FLEET-0007). A refused POST that still
/// wrote a message would satisfy any status assertion; what has to be true is that nothing was signed and
/// nothing was merged. The log is read back through a desktop-plane GET, which reaches the EF read model
/// rather than the CRDT delta stream a peer actually syncs — a sound PROXY for "nothing was merged", not
/// the peer path itself: the append handler writes EF first and appends to the CRDT second, so an empty EF
/// log entails no local CRDT append.
/// </para>
/// <para>
/// This is the PLANE half. Card earlier repository ticket #3379 converts <c>CommsRoutes</c>'s fallback author to the acting
/// member — the ATTRIBUTION half, on a different file.
/// </para>
/// </remarks>
public sealed class CommsWebPlaneFenceTests : IAsyncLifetime
{
    private const string CallerToken = "comms-web-plane-fence-caller-token-0001";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-comms-entropy-000000";
    private const string OperatorParty = "os:comms-fence-operator";
    private static readonly Guid Team = NodeTestActiveTeam.TestTeamId.Value;
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
    public async Task A_members_message_is_never_signed_or_merged()
    {
        var client = await StartAsync();

        var refused = await SendAsMemberAsync(client, NewPostRequest("a message the member should not be able to send"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using (var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                WebPlaneUnavailableRouteFence.UnavailableCode,
                body.RootElement.GetProperty("code").GetString());
        }

        // THE assertion. Read the durable log back through the desktop plane. The append handler writes EF
        // before the CRDT, so an empty read model entails nothing was merged either. Before the fence this
        // held one message whose author was OperatorParty.
        var log = await ReadLogAsDesktopOperatorAsync(client);
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_member_cannot_read_the_teams_log_either()
    {
        // The gated GET this consumer owes on its own account, rather than trusting another card's fence test
        // on a different route family. A GET here returns the whole team channel — a read scope the member
        // does not have under MTW-2.
        var client = await StartAsync();

        // Seed one real message from the desktop plane so an empty log cannot pass for a refusal.
        var seeded = await SendAsDesktopOperatorAsync(client, NewPostRequest("desktop message"));
        Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

        var refused = await SendAsMemberAsync(
            client, new HttpRequestMessage(HttpMethod.Get, CommsRoutes.RouteBase));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_member_cannot_reach_the_dm_routes()
    {
        // The DM routes are mapped only when the DM feature flag is on, which is the PRODUCTION default —
        // the flag is a kill-switch (HARBORLINE_COMMS_DM_DISABLED), not an opt-in. A harness that disabled it
        // would leave the sealed-message half of the family driven by nothing, which is the same harm class
        // the card is about.
        var client = await StartAsync();

        var refused = await SendAsMemberAsync(
            client, new HttpRequestMessage(HttpMethod.Get, $"{CommsRoutes.RouteBase}/dm/roster"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_desktop_operator_still_sends_and_reads_as_the_roster_bound_member()
    {
        // THE control, and it is doing two jobs. A change that refused both planes would satisfy every
        // assertion above while making messaging impossible — so the desktop post must still land. And the
        // author it lands under must still be the roster-bound active member, or the fence would be masking
        // a regression in the very capture it exists to contain.
        var client = await StartAsync();

        var created = await SendAsDesktopOperatorAsync(client, NewPostRequest("hello from the desktop"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var log = await ReadLogAsDesktopOperatorAsync(client);
        var message = Assert.Single(log);
        Assert.Equal(OperatorParty, message.GetProperty("authorPartyId").GetString());
        Assert.Equal("hello from the desktop", message.GetProperty("body").GetString());
    }

    private static HttpRequestMessage NewPostRequest(string body) =>
        new(HttpMethod.Post, CommsRoutes.RouteBase) { Content = JsonContent.Create(new { body }) };

    /// <summary>Drives the request the way the Harborline App's web plane does — a selected-session cookie only.</summary>
    private static Task<HttpResponseMessage> SendAsMemberAsync(HttpClient client, HttpRequestMessage request)
    {
        request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsDesktopOperatorAsync(HttpClient client, HttpRequestMessage request)
    {
        request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
        return client.SendAsync(request);
    }

    private static async Task<List<JsonElement>> ReadLogAsDesktopOperatorAsync(HttpClient client)
    {
        var response = await SendAsDesktopOperatorAsync(
            client, new HttpRequestMessage(HttpMethod.Get, CommsRoutes.RouteBase));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>
    /// A real listener serving the REAL <see cref="HostedCommsApiEndpoint"/>, driven through its own
    /// <c>StartAsync</c> so the startup capture and the fence installation are the production ones, over a
    /// REAL SQLite-backed <see cref="CommsConversationRegistry"/> so an appended message is genuinely durable.
    /// </summary>
    private async Task<HttpClient> StartAsync()
    {
        var signer = new NodePrincipalSigner(Seed(0x3b));
        _disposables.Add(signer);

        // The roster binds the operator party to THIS signer's key — the consistency the endpoint asserts
        // before mapping, and what makes the desktop control's author assertion meaningful.
        var roster = new NodeTeamRoster(MemberRoster.Genesis(
            Team, OperatorParty, signer.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid()));

        var dir = Path.Combine(Path.GetTempPath(), $"harborline-comms-fence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging(b => b.ClearProviders());
        services.AddDbContextFactory<NodeLocalCommsDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "comms.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        services.AddSingleton<IActiveTeamAccessor>(NodeTestActiveTeam.Accessor);
        services.AddSingleton(new NodeCallerSessionToken(CallerToken));
        services.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());
        services.AddHarborlineDeltaRouter();
        var sp = services.BuildServiceProvider();
        _providers.Add(sp);

        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var registry = new CommsConversationRegistry(
            sp.GetRequiredService<ICrdtEngine>(), factory, new Ed25519Verifier(),
            sp.GetRequiredService<IDeltaRouter>(), sp.GetRequiredService<ILoggerFactory>(),
            rosterBinding: roster.ForgeProofBinding);
        _async.Add(registry);

        var app = new SharedHostedWebApp(
            sp,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            sp.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            sp.GetRequiredService<TimeProvider>());
        _apps.Add(app);

        // The REAL hosted endpoint. Its StartAsync performs the startup capture AND installs the fence in the
        // same call, so a change that separates them is visible here.
        var endpoint = new HostedCommsApiEndpoint(
            app, registry, signer, NodeTestActiveTeam.Accessor, sp.GetRequiredService<NodeCallerSessionToken>(),
            roster, new CommsDmFeatureFlag(), sp.GetRequiredService<TimeProvider>(),
            NullLogger<HostedCommsApiEndpoint>.Instance);
        await endpoint.StartAsync(CancellationToken.None);

        await app.StartAsync(CancellationToken.None);
        var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        _clients.Add(client);
        return client;
    }

    private static byte[] Seed(byte fill)
    {
        var seed = new byte[32];
        Array.Fill(seed, fill);
        return seed;
    }

    /// <summary>
    /// Materializes ONE real <see cref="SelectedSessionRequestPrincipal"/> for the known handle — the only
    /// substituted seam, and it stands UPSTREAM of the route family under test. Its presence is what makes the
    /// listener open the attribution scope the fence reads, and what sets the gate-passed marker that
    /// <c>callerAuth.Validate</c> answers <c>Allow</c> to.
    /// </summary>
    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    accountId: "account-comms-fence",
                    tenantId: new TenantId(Team.ToString("D")),
                    principalUserId: new PrincipalUserId("principal-comms-fence"),
                    canonicalParty: new CanonicalPartyReference("party-member-comms-fence"),
                    membershipId: "membership-comms-fence",
                    membershipOwnerVersion: 2,
                    pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-comms-fence", 3)],
                    authorizationEpoch: 5,
                    sessionCorrelationId: "session-comms-fence",
                    coordinationCorrelationId: "coordination-comms-fence")
                : null);
    }
}
