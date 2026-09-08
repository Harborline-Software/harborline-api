using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Scheduling;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>Proves an authenticated LAN device cannot borrow desktop grants on a gated route.</summary>
public sealed class DevicePlaneRouteGatingTests : IAsyncLifetime
{
    private const string DeviceBearer = "device-plane-route-gating-bearer";
    private static readonly TeamId Team = new(Guid.Parse("36630000-0000-0000-0000-000000000002"));
    private static readonly string Route =
        $"{SchedulingDefinitionRoutes.RouteBase}/device-plane/draft";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "device-plane-route-gating-" + Guid.NewGuid().ToString("N") + ".db");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IDbContextFactory<NodeLocalSchedulingDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        var activeTeam = new FixedActiveTeamAccessor(
            new TeamContext(Team, "Device-plane test team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        var memberships = new InMemoryTeamRegistry();
        await memberships.AddMembershipAsync(
            ActiveTeamAuthorizationContext.NodeOperator,
            new TeamMembership(
                Team.Value,
                "Device-plane test team",
                TeamRolePermissions.DisplayName(TeamRole.Admin),
                KeyFingerprint.FromPublicKey(Team.Value.ToByteArray()),
                TeamRole.Admin));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IActiveTeamAccessor>(activeTeam);
        builder.Services.AddSingleton<IMutableTeamRegistry>(memberships);
        builder.Services.AddSingleton<ITeamRegistry>(memberships);
        builder.Services.AddSingleton<ISelectedSessionPermissionResolver,
            FailClosedSelectedSessionPermissionResolver>();
        builder.Services.AddSingleton<ActiveTeamAuthorizationContext>();
        builder.Services.AddDbContextFactory<NodeLocalSchedulingDbContext>(options => options.UseSqlite(
            $"Data Source={_dbPath};Pooling=False"));
        builder.Services.AddScoped<SelectedSessionTenantContext>();
        builder.Services.AddScoped<IAuthorizationContext>(sp =>
            sp.GetRequiredService<SelectedSessionTenantContext>());

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>();
        await using (var db = await _factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();

        _app.Use(async (context, next) =>
        {
            context.Features.Set(LanListenerRequestFeature.Instance);
            await next(context).ConfigureAwait(false);
        });
        SharedHostedWebApp.UseLanListenerGate(
            _app,
            new FixedLanDeviceSessionAuthority(),
            new LanConnectionRateLimiter(TimeProvider.System));

        var store = new NodeSchedulingDraftStore(_factory, TimeProvider.System);
        SchedulingDefinitionRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            store,
            new SchedulingDraftValidator(),
            parties: null!,
            activeTeam,
            new FixedCurrentUser(),
            bookingService: null!,
            eventStore: null!,
            calendarStore: null!,
            availabilityStore: null!,
            freeBusyService: null!,
            TimeProvider.System);

        await _app.StartAsync(CancellationToken.None);
        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    [Fact(DisplayName = "device plane: gated scheduling route refuses desktop grants before handler")]
    public async Task DevicePlane_SchedulingMutation_IsForbidden_AndHandlerDoesNotRun()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Route)
        {
            Content = JsonContent.Create(new
            {
                expectedRevision = 0,
                definition = new
                {
                    schema = "harborline.scheduling-definition-draft/v0",
                    title = "must not persist",
                    timezone = "America/New_York",
                    activities = new[] { new { id = "appointment", durationMinutes = 30 } },
                    bufferBeforeMinutes = 0,
                    bufferAfterMinutes = 0,
                    minimumLeadTimeMinutes = 0,
                    resourceRequirements = Array.Empty<object>(),
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeviceBearer);

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal(Permission.SchedulingAuthor, body.GetProperty("permission").GetString());

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.Drafts.ToListAsync());
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private sealed class FixedLanDeviceSessionAuthority : ILanDeviceSessionAuthority
    {
        public ValueTask<LanDevicePrincipal?> AuthenticateAsync(
            Microsoft.AspNetCore.Http.HttpContext context,
            string bearer,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<LanDevicePrincipal?>(
                string.Equals(bearer, DeviceBearer, StringComparison.Ordinal)
                    ? new LanDevicePrincipal("device-1", Team.Value.ToString("D"), "device-principal")
                    : null);
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class FixedCurrentUser : ICurrentUser
    {
        public string UserId => "device-plane-test-actor";
        public IReadOnlyList<string> Roles { get; } = Array.Empty<string>();
    }
}
