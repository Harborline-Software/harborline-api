using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Spatial;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

public sealed class SpatialFrameSelectedTenantTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("10830000-0000-0000-0000-00000000000a"));
    private readonly List<AuthorizationGateRequest> _requests = [];
    private readonly TrackingFrames _frames = new();
    private SelectedSessionRequestPrincipal? _selected;
    private bool _boundWeb;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private ServiceProvider _teamServices = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestDesktopOperator();
        builder.Services.AddTestKernelClock();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser>(services => new RequestUser(
            services.GetRequiredService<IHttpContextAccessor>()));
        var source = new TestRouteGate.ScopedGrantSource(request =>
        {
            _requests.Add(request);
            return request.Act.Operation.Value == "spatial:read";
        }, []);
        builder.Services.AddSingleton(new AuthorizationGate(source, new EmptyRecordStandingResolver(), source));
        _teamServices = new ServiceCollection().BuildServiceProvider();
        var activeTeam = new FixedActiveTeam(new TeamContext(TeamA, "Team A", _teamServices, TimeProvider.System));
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (_selected is not null) http.Features.Set(_selected);
            using var web = _boundWeb
                ? NodeCallerAttributionScope.EnterDevice("fixture-device", "tenant-b", "fixture-principal")
                : null;
            await next(http);
        });
        SpatialFrameRoutes.Map(_app, _frames, activeTeam);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_teamServices is not null) await _teamServices.DisposeAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/1")]
    public async Task Selected_session_reads_its_tenant_and_binds_correlation(string suffix)
    {
        _selected = new SelectedSessionRequestPrincipal("account-b", new TenantId("tenant-b"),
            new PrincipalUserId("selected-holder"), new CanonicalPartyReference("party-b"),
            "membership-b", 1, [new PinnedGrantOwnerVersion("grant-b", 1)], 1, "session-b", "coordination-b");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/spatial-frames/hull-001/Hull_Datum" + suffix);
        request.Headers.Add("X-Correlation-ID", "10830000-0000-4000-8000-000000000001");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var frame = suffix.Length == 0 ? Assert.Single(body.GetProperty("frames").EnumerateArray()) : body;
        Assert.Equal("hull-001", frame.GetProperty("anchor").GetString());
        Assert.Equal("hull-datum", frame.GetProperty("frameCode").GetString());
        Assert.Equal(1, frame.GetProperty("frameEpoch").GetInt64());
        Assert.Equal("tenant-b origin", frame.GetProperty("originDescription").GetString());
        var read = Assert.Single(_frames.Reads);
        Assert.Equal("tenant-b", read.Tenant);
        Assert.Equal(suffix.Length == 0 ? "list" : "find", read.Method);
        Assert.Equal("principal:selected-holder", read.Context.ActorRef);
        Assert.True(read.Context.UnsealsGoverned);
        var decision = Assert.Single(_requests);
        Assert.Equal("tenant-b", decision.Tenant.Value);
        Assert.Equal("selected-holder", decision.Principal.Value);
        Assert.Equal("spatial:read", decision.Act.Operation.Value);
        Assert.Equal("hull-001", decision.Target.RecordId);
        Assert.Equal(Guid.Parse("10830000-0000-4000-8000-000000000001"), decision.CorrelationId);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("/1", false)]
    [InlineData("", true)]
    [InlineData("/1", true)]
    public async Task Unresolved_selected_request_stops_before_authorization_and_store(string suffix, bool boundWeb)
    {
        _boundWeb = boundWeb;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/spatial-frames/hull-001/hull-datum" + suffix);
        if (!boundWeb) request.Headers.Add("Cookie", "__Host-hl-selected=unresolved");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_frames.Reads);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/1")]
    public async Task Caller_without_selected_session_retains_active_team_reads(string suffix)
    {
        using var response = await _client.GetAsync("/api/local-node/spatial-frames/hull-001/hull-datum" + suffix);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var frame = suffix.Length == 0 ? Assert.Single(body.GetProperty("frames").EnumerateArray()) : body;
        Assert.Equal("team-a origin", frame.GetProperty("originDescription").GetString());
        Assert.Equal("10830000-0000-0000-0000-00000000000a", Assert.Single(_frames.Reads).Tenant);
        Assert.Equal("10830000-0000-0000-0000-00000000000a", Assert.Single(_requests).Tenant.Value);
    }

    private sealed record Read(string Method, string Tenant, SpatialFrameReadContext Context);

    private sealed class TrackingFrames : ISpatialFrameDescriptorStore
    {
        public List<Read> Reads { get; } = [];
        private SpatialFrameDescriptor Descriptor(TenantId tenant, RegistryEntityId anchor, string code, long epoch,
            SpatialFrameReadContext context, string method)
        {
            Reads.Add(new(method, tenant.Value, context));
            var origin = tenant.Value switch
            {
                "tenant-b" => "tenant-b origin",
                "10830000-0000-0000-0000-00000000000a" => "team-a origin",
                _ => throw new InvalidOperationException("Unexpected fixture tenant"),
            };
            return new(tenant, anchor, code, epoch, "x-fwd-y-stbd-z-down", origin, "metre", null,
                new SpatialFrameMintAttestation("fixture", Guid.Empty, DateTimeOffset.UnixEpoch, null, "fixture", 1, 0, null));
        }
        public Task<SpatialFrameDescriptor?> FindAsync(TenantId tenant, RegistryEntityId anchor, string frameCode,
            long frameEpoch, SpatialFrameReadContext context, CancellationToken ct = default) =>
            Task.FromResult<SpatialFrameDescriptor?>(Descriptor(tenant, anchor, frameCode, frameEpoch, context, "find"));
        public Task<IReadOnlyList<SpatialFrameDescriptor>> ListAsync(TenantId tenant, RegistryEntityId anchor,
            string frameCode, SpatialFrameReadContext context, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SpatialFrameDescriptor>>([Descriptor(tenant, anchor, frameCode, 1, context, "list")]);
        public Task<SpatialFrameDescriptor> MintAsync(SpatialFrameMintRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SpatialFrameQuarantineRecord>> ListQuarantinedAsync(TenantId tenant, RegistryEntityId anchor,
            string frameCode, SpatialFrameReadContext context, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RequestUser(IHttpContextAccessor accessor) : ICurrentUser
    {
        public string UserId => accessor.HttpContext?.Features.Get<SelectedSessionRequestPrincipal>()?.PrincipalUserId.Value
            ?? "desktop-operator-under-test";
        public IReadOnlyList<string> Roles => [];
    }

    private sealed class FixedActiveTeam(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
