using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests;

using Xunit;

using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// End-to-end conformance for the read-only spatial-frame resolution routes (card G4.1;
/// ADR 0168 D2-A5): sealed columns round-trip through the store seam onto the wire, tenant
/// scoping is opaque, unauthenticated and unauthorized callers are refused, the mint
/// attestation is not projected, and quarantined (losing) content is never exposed.
/// </summary>
[Collection(HomeEpochFenceStaticHookCollection.Name)]
public sealed class SpatialFrameRouteTests : IAsyncLifetime
{
    private const string CallerToken = "spatial-route-caller-token";
    private const string SelectedHandle = "spatial-route-selected-handle";
    private static readonly TeamId Team = new(Guid.Parse("47470000-0000-0000-0000-000000000001"));
    private static readonly TenantId Tenant = new(Team.Value.ToString());
    private static readonly TenantId OtherTenant = new("tenant-other-org");
    private static readonly RegistryEntityId Anchor = new("hull-001");

    private string _dir = null!;
    private ServiceProvider _outer = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodePrincipalSigner _signer = null!;
    private FoundationBackedSpatialFrameDescriptorStore _store = null!;
    private InMemoryRegistryAuditLog _audit = null!;
    private SharedHostedWebApp _app = null!;
    private HttpClient _client = null!;
    private readonly MutablePermissionResolver _resolver = new();

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-spatial-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "spatial-routes.db")};Pooling=False";

        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        _signer = new NodePrincipalSigner(seed);

        var activeTeam = new FixedActiveTeamAccessor(
            new TeamContext(Team, "Spatial route test team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        var memberships = new InMemoryTeamRegistry();
        await memberships.AddMembershipAsync(
            ActiveTeamAuthorizationContext.NodeOperator,
            new TeamMembership(
                Team.Value,
                "Spatial route test team",
                TeamRolePermissions.DisplayName(TeamRole.Admin),
                KeyFingerprint.FromPublicKey(Team.Value.ToByteArray()),
                TeamRole.Admin));

        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging(logging => logging.ClearProviders());
        outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
        outer.AddSingleton<IMutableTeamRegistry>(memberships);
        outer.AddSingleton<ITeamRegistry>(memberships);
        outer.AddSingleton<ISelectedSessionPermissionResolver>(_resolver);
        outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
        outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());
        // Ticket 205 slice 4: the spatial-frame read guard resolves at the gate; SharedHostedWebApp bridges
        // this outer singleton into the inner request container the route reads. The gate follows the SAME
        // selected-session permission set the fixture flips, so the role-map and no-spatial:read teeth still
        // move the verdict.
        outer.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            (principal, permission) => string.Equals(principal, Harborline.Api.Blocks.AccessGrant.AccessGrantAuthorizationSeed.NodeOperatorPrincipal, StringComparison.Ordinal)
                || _resolver.Holds(permission)));
        outer.AddSingleton<IHarborlineEntityModule, HomeEpochEntityModule>();
        outer.AddSingleton<IHarborlineEntityModule, SpatialFrameEntityModule>();
        outer.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        outer.AddNodeFinancialPosting();
        _outer = outer.BuildServiceProvider();

        _factory = _outer.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var rootSeed = new byte[32];
        Random.Shared.NextBytes(rootSeed);
        var sealer = new SpatialFramePiiFieldSealer(
            new Harborline.Api.LocalNodeHost.Data.Search.Vector.RootSeedTenantKeyProvider(rootSeed));
        _audit = new InMemoryRegistryAuditLog();
        var port = new NodeEfSpatialFrameDescriptorPort(
            _factory, _audit, sealer, _signer, clock: TimeProvider.System);
        _store = new FoundationBackedSpatialFrameDescriptorStore(
            port, new NodeHomeClaimFrameEpochAuthority(_factory, _signer), _signer.Signer, clock: TimeProvider.System);

        _app = new SharedHostedWebApp(
            _outer,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            _outer.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            _outer.GetRequiredService<TimeProvider>());
        _app.MapApiRoutes(routes => SpatialFrameRoutes.Map(
            routes.MapSelectedSessionProductGroup(),
            _store,
            activeTeam));
        await _app.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri(_app.SelectedUrl!) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
        if (_outer is not null)
            await _outer.DisposeAsync();
        _signer?.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private async Task SeedHomeClaimAsync(TenantId tenant)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Add(new HomeEpochRecord
        {
            TenantId = tenant.Value,
            EpochNumber = 1,
            PreviousEpochNumber = 0,
            HomeDeviceId = _signer.NodePublicKey,
            PromotionKind = HomePromotionKind.PlannedHandoff,
            IssuedAt = DateTimeOffset.UtcNow,
            Nonce = Guid.NewGuid(),
            IssuerId = _signer.NodePublicKey,
            Signature = "test-seeded",
        });
        await ctx.SaveChangesAsync();
    }

    private static SpatialFrameMintRequest Request(TenantId tenant, long previousEpoch = 0) =>
        new(tenant, Anchor, "hull-datum", previousEpoch,
            AxisConvention: "x-fwd-y-stbd-z-down",
            OriginDescription: "aft perpendicular at baseline",
            LengthUnit: "metre",
            Georeference: new SpatialFrameGeoreference(
                ObservedAt: "2026-08-05T12:34:56Z",
                GeodeticCrs: "EPSG:4979",
                OriginPosition: new[] { 51.500123, -0.250456, 12.75 },
                Orientation: null,
                PoseBasis: null));

    private HttpRequestMessage SessionGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(
            "Cookie", $"{WebSessionCookieNames.Selected}={SelectedHandle}");
        return request;
    }

    private HttpRequestMessage DesktopGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(
            NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
        return request;
    }

    [Fact(DisplayName = "GET list + get return the minted defining fields — sealed columns round-trip; attestation absent")]
    public async Task Read_ReturnsMintedFields_SealedColumnsRoundTrip()
    {
        await SeedHomeClaimAsync(Tenant);
        await _store.MintAsync(Request(Tenant, previousEpoch: 0));
        await _store.MintAsync(Request(Tenant, previousEpoch: 1) with
        {
            OriginDescription = "forward perpendicular at baseline",
            Georeference = null,
        });

        _resolver.Set(Permission.SpatialRead);
        var list = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadFromJsonAsync<JsonElement>();
        var frames = listBody.GetProperty("frames");
        Assert.Equal(2, frames.GetArrayLength());
        Assert.Equal(1, frames[0].GetProperty("frameEpoch").GetInt64());
        Assert.Equal(2, frames[1].GetProperty("frameEpoch").GetInt64());
        Assert.Equal("aft perpendicular at baseline", frames[0].GetProperty("originDescription").GetString());
        Assert.Equal("EPSG:4979", frames[0].GetProperty("georeference").GetProperty("geodeticCrs").GetString());

        var get = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var raw = await get.Content.ReadAsStringAsync();
        var one = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("x-fwd-y-stbd-z-down", one.GetProperty("axisConvention").GetString());
        Assert.Equal("aft perpendicular at baseline", one.GetProperty("originDescription").GetString());
        Assert.Equal("metre", one.GetProperty("lengthUnit").GetString());
        Assert.Equal(51.500123, one.GetProperty("georeference").GetProperty("originPosition")[0].GetDouble());

        // The signed persistence envelope is deliberately NOT projected (0168 OQ-1 amendment).
        Assert.DoesNotContain("signature", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attestation", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_signer.NodePublicKey, raw, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Audit@Read actorRef is the request PRINCIPAL (principal: scheme) — never a device id")]
    public async Task Read_UnsealAudit_AttributesTheRequestPrincipal_NeverADevice()
    {
        await SeedHomeClaimAsync(Tenant);
        await _store.MintAsync(Request(Tenant, previousEpoch: 0));

        _resolver.Set(Permission.SpatialRead);
        var ok = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // The route's authorized read unseals the governed cells, so it owes Audit@Read rows —
        // attributed to the SERVER-DERIVED principal behind the session (the canonical party the
        // inner PEP bound), carried with the "principal:" scheme. A static node/device id here
        // would attribute every unsealing to the machine (deep-review F4).
        var unsealRows = _audit.ForTenant(Tenant)
            .Where(e => e.Op == RegistryOp.SpatialFrameDescriptorPiiUnsealed).ToList();
        var row = Assert.Single(unsealRows);
        Assert.Equal("principal:party:spatial-route", row.ActorRef);
        Assert.StartsWith("principal:", row.ActorRef);
        Assert.DoesNotContain(_signer.NodePublicKey, row.ActorRef, StringComparison.Ordinal);
        // Identity triple only — never the governed prose.
        Assert.Equal("anchor=hull-001;frameCode=hull-datum;frameEpoch=1", row.Detail);
        Assert.DoesNotContain("aft perpendicular", row.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Tenant scoping is opaque: another tenant's frames are an empty list and a 404")]
    public async Task Read_OtherTenantsFrames_Invisible()
    {
        await SeedHomeClaimAsync(OtherTenant);
        await _store.MintAsync(Request(OtherTenant, previousEpoch: 0));

        _resolver.Set(Permission.SpatialRead);
        var list = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, listBody.GetProperty("frames").GetArrayLength());

        var get = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact(DisplayName = "Unauthenticated is 401; an authenticated session without spatial:read is 403")]
    public async Task Read_Unauthenticated_And_Unauthorized_Refused()
    {
        await SeedHomeClaimAsync(Tenant);
        await _store.MintAsync(Request(Tenant, previousEpoch: 0));

        // No selected-session cookie, no caller token: the listener refuses before the handler.
        using var anonymous = new HttpRequestMessage(
            HttpMethod.Get, "/api/local-node/spatial-frames/hull-001/hull-datum");
        var unauthenticated = await _client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // A live session whose permission set lacks spatial:read is denied with the stable code.
        _resolver.Set();
        var denied = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedBody = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", deniedBody.GetProperty("code").GetString());
        Assert.Equal(Permission.SpatialRead, deniedBody.GetProperty("permission").GetString());

        var deniedGet = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedGet.StatusCode);

        // The desktop plane (caller token; operator bootstrap membership) still reads.
        var desktop = await _client.SendAsync(
            DesktopGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.OK, desktop.StatusCode);
    }

    [Fact(DisplayName = "The coarse records:read alone no longer opens the surface — the card-3777 tightening")]
    public async Task Read_RecordsReadAlone_IsNoLongerSufficient()
    {
        await SeedHomeClaimAsync(Tenant);
        await _store.MintAsync(Request(Tenant, previousEpoch: 0));

        // THE BEHAVIOR CHANGE (card 3777, CIC ruling 2026-08-06): before the tightening, the routes
        // gated on records:read, so ANY reader — including the viewer floor — could resolve site
        // coordinates. records:read without spatial:read is now a 403 on both routes.
        _resolver.Set(TeamRolePermissions.RecordsRead);
        var list = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Permission.SpatialRead, body.GetProperty("permission").GetString());

        var get = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact(DisplayName = "Role map (CIC 2026-08-06): Viewer composition is 403; Member and Admin are 200")]
    public async Task Read_RoleCompositions_ViewerRefused_MemberAndAdminRead()
    {
        await SeedHomeClaimAsync(Tenant);
        await _store.MintAsync(Request(Tenant, previousEpoch: 0));

        // Viewer (the read-only floor) deliberately does NOT hold spatial:read — 403 on both routes.
        _resolver.Set([.. PermissionCompositions.Viewer.Permissions]);
        var viewerList = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.Forbidden, viewerList.StatusCode);
        var viewerGet = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.Forbidden, viewerGet.StatusCode);

        // Member (partner field crews are Members) reads both routes.
        _resolver.Set([.. PermissionCompositions.Member.Permissions]);
        var memberList = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.OK, memberList.StatusCode);
        var memberGet = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.OK, memberGet.StatusCode);

        // Admin reads both routes.
        _resolver.Set([.. PermissionCompositions.Admin.Permissions]);
        var adminList = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);
        var adminGet = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/1"));
        Assert.Equal(HttpStatusCode.OK, adminGet.StatusCode);
    }

    [Fact(DisplayName = "Quarantined (losing) mints are NOT exposed: not in the series, content absent from the wire")]
    public async Task Read_QuarantinedContent_NeverExposed()
    {
        await SeedHomeClaimAsync(Tenant);
        await _store.MintAsync(Request(Tenant, previousEpoch: 0)); // tip = 1

        // A stale mint quarantines its losing content (D2-A6 layer 2).
        await Assert.ThrowsAsync<SpatialFrameEpochMintRejectedException>(
            () => _store.MintAsync(Request(Tenant, previousEpoch: 0) with
            {
                OriginDescription = "quarantined losing origin prose",
            }));

        _resolver.Set(Permission.SpatialRead);
        var list = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var raw = await list.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal(1, body.GetProperty("frames").GetArrayLength());
        Assert.DoesNotContain("quarantined losing origin prose", raw, StringComparison.OrdinalIgnoreCase);

        // No quarantine route exists under the surface at all.
        var probe = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/quarantine"));
        Assert.Equal(HttpStatusCode.BadRequest, probe.StatusCode); // parses as an invalid epoch, not a surface
    }

    [Fact(DisplayName = "frameCode: garbage is a static 400; a lexical variant normalizes and the envelope echoes the NORMALIZED form")]
    public async Task Read_FrameCode_NormalizedOnceAndEchoedNormalized()
    {
        await SeedHomeClaimAsync(Tenant);
        await _store.MintAsync(Request(Tenant, previousEpoch: 0)); // stored as 'hull-datum'

        _resolver.Set(Permission.SpatialRead);

        // Garbage codes 400 with the stable error, never a 500 (F2).
        foreach (var bad in new[] { "hull%20datum%21", "---", "hull.datum" })
        {
            var refused = await _client.SendAsync(
                SessionGet($"/api/local-node/spatial-frames/hull-001/{bad}"));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_frame_code", body.GetProperty("error").GetString());
        }

        // A lexical variant of the stored code resolves, and the envelope echoes the
        // normalized identity — never the raw caller spelling (F3, [A14]).
        var variant = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/Hull_Datum"));
        Assert.Equal(HttpStatusCode.OK, variant.StatusCode);
        var raw = await variant.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("hull-datum", envelope.GetProperty("frameCode").GetString());
        Assert.Equal(1, envelope.GetProperty("frames").GetArrayLength());
        Assert.DoesNotContain("Hull_Datum", raw, StringComparison.Ordinal);

        var variantGet = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/Hull_Datum/1"));
        Assert.Equal(HttpStatusCode.OK, variantGet.StatusCode);
        var one = await variantGet.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hull-datum", one.GetProperty("frameCode").GetString());
    }

    [Fact(DisplayName = "A non-numeric or non-positive epoch is a static 400")]
    public async Task Read_InvalidEpoch_BadRequest()
    {
        _resolver.Set(Permission.SpatialRead);
        var zero = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/0"));
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        var text = await _client.SendAsync(
            SessionGet("/api/local-node/spatial-frames/hull-001/hull-datum/not-an-epoch"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
    }

    private sealed class MutablePermissionResolver : ISelectedSessionPermissionResolver
    {
        private PermissionSet? _permissions;

        public void Set(params string[]? permissions) =>
            _permissions = permissions is null ? null : PermissionSet.From(permissions);

        public ValueTask<PermissionSet?> ResolveAsync(
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_permissions);

        /// <summary>The same set, read by the ticket-205 gate this fixture registers.</summary>
        public bool Holds(string permission) => _permissions?.Contains(permission) == true;
    }

    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, SelectedHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    "spatial-route-account",
                    new TenantId(Team.Value.ToString("D")),
                    new PrincipalUserId("spatial-route-principal"),
                    new CanonicalPartyReference("party:spatial-route"),
                    "spatial-route-membership",
                    1,
                    [new PinnedGrantOwnerVersion("spatial-route-grant", 1)],
                    1,
                    "spatial-route-session",
                    "spatial-route-coordination")
                : null);
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
