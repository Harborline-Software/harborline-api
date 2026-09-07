using System.Net;
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
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Scheduling;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Proves the production selected-session middleware binds the inner PEP before a real gated route runs.
/// </summary>
public sealed class RouteGatingPepWiringTests : IAsyncLifetime
{
    private const string CallerToken = "l5-route-gating-caller-token";
    private const string SelectedHandle = "l5-route-gating-selected-handle";
    private const string Route = "/api/local-node/scheduling/definitions/wiring/draft";
    private static readonly TeamId Team = new(Guid.Parse("36630000-0000-0000-0000-000000000001"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "l5-route-gating-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MutablePermissionResolver _resolver = new();
    private ServiceProvider _outer = null!;
    private SharedHostedWebApp _app = null!;
    private HttpClient _client = null!;
    private NodeSchedulingDraftStore _store = null!;

    public async Task InitializeAsync()
    {
        var activeTeam = new FixedActiveTeamAccessor(
            new TeamContext(Team, "L5 test team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        var memberships = new InMemoryTeamRegistry();
        await memberships.AddMembershipAsync(
            ActiveTeamAuthorizationContext.NodeOperator,
            new TeamMembership(
                Team.Value,
                "L5 test team",
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
        // Ticket 205 slice 4: the scheduling guards resolve at the gate. The desktop operator holds the
        // operation; a signed-in web member holds exactly what the per-request PEP resolver says, so the
        // narrowed/revoked session teeth still move the verdict.
        outer.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            (principal, permission) => string.Equals(principal, Harborline.Api.Blocks.AccessGrant.AccessGrantAuthorizationSeed.NodeOperatorPrincipal, StringComparison.Ordinal)
                || _resolver.Holds(permission)));
        outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
        outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());
        outer.AddDbContextFactory<NodeLocalSchedulingDbContext>(options => options.UseSqlite(
            $"Data Source={_dbPath};Pooling=False"));
        outer.AddNodeFinancialPosting();
        _outer = outer.BuildServiceProvider();

        var factory = _outer.GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        _store = new NodeSchedulingDraftStore(factory, TimeProvider.System);

        _app = new SharedHostedWebApp(
            _outer,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            _outer.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            _outer.GetRequiredService<TimeProvider>());
        var currentUser = new FixedCurrentUser("selected-session-actor");
        _app.MapApiRoutes(routes => SchedulingDefinitionRoutes.Map(
            routes.MapDeviceReachableProductDataGroup(),
            _store,
            new SchedulingDraftValidator(),
            parties: null!,
            activeTeam,
            currentUser,
            bookingService: null!,
            eventStore: null!,
            calendarStore: null!,
            availabilityStore: null!,
            freeBusyService: null!,
            TimeProvider.System));
        await _app.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri(_app.SelectedUrl!) };
    }

    [Fact]
    public async Task SelectedSession_BindAsync_ResolvesInnerPep_PerRequest_AndDesktopBootstrapStillWorks()
    {
        _resolver.Set(Permission.SchedulingAuthor);
        using var selected = NewRequest(HttpMethod.Put, Route, Definition("selected allowed"));
        var allowed = await _client.SendAsync(selected);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(_resolver.Calls > 0);

        _resolver.Set();
        using var narrowed = NewRequest(HttpMethod.Put, "/api/local-node/scheduling/definitions/narrowed/draft",
            Definition("selected denied"));
        var denied = await _client.SendAsync(narrowed);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedBody = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", deniedBody.GetProperty("code").GetString());
        Assert.Equal(Permission.SchedulingAuthor, deniedBody.GetProperty("permission").GetString());

        _resolver.Set(null);
        using var revoked = NewRequest(HttpMethod.Put, "/api/local-node/scheduling/definitions/revoked/draft",
            Definition("selected revoked"));
        var revokedResponse = await _client.SendAsync(revoked);
        Assert.Equal(HttpStatusCode.Forbidden, revokedResponse.StatusCode);

        using var desktop = new HttpRequestMessage(HttpMethod.Put,
            "/api/local-node/scheduling/definitions/desktop/draft")
        {
            Content = JsonContent.Create(Definition("desktop allowed")),
        };
        desktop.Headers.TryAddWithoutValidation(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
        var desktopResponse = await _client.SendAsync(desktop);
        Assert.Equal(HttpStatusCode.OK, desktopResponse.StatusCode);

        await using var db = await _outer.GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(2, await db.Drafts.CountAsync());
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
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"{WebSessionCookieNames.Selected}={SelectedHandle}");
        return request;
    }

    private static object Definition(string title) => new
    {
        expectedRevision = 0,
        definition = new
        {
            schema = "harborline.scheduling-definition-draft/v0",
            title,
            timezone = "America/New_York",
            activities = new[] { new { id = "appointment", durationMinutes = 30 } },
            bufferBeforeMinutes = 0,
            bufferAfterMinutes = 0,
            minimumLeadTimeMinutes = 0,
            resourceRequirements = Array.Empty<object>(),
        },
    };

    private sealed class MutablePermissionResolver : ISelectedSessionPermissionResolver
    {
        private PermissionSet? _permissions;

        internal int Calls { get; private set; }

        public void Set(params string[]? permissions) =>
            _permissions = permissions is null ? null : PermissionSet.From(permissions);

        public ValueTask<PermissionSet?> ResolveAsync(
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(_permissions);
        }

        /// <summary>The same set, read by the ticket-205 gate this fixture registers.</summary>
        internal bool Holds(string permission) => _permissions?.Contains(permission) == true;
    }

    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, SelectedHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    "l5-route-gating-account",
                    new TenantId(Team.Value.ToString("D")),
                    new PrincipalUserId("l5-route-gating-principal"),
                    new CanonicalPartyReference("party:l5-route-gating"),
                    "l5-route-gating-membership",
                    1,
                    [new PinnedGrantOwnerVersion("l5-route-gating-grant", 1)],
                    1,
                    "l5-route-gating-session",
                    "l5-route-gating-coordination")
                : null);
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class FixedCurrentUser(string userId) : ICurrentUser
    {
        public string UserId { get; } = userId;
        public IReadOnlyList<string> Roles { get; } = Array.Empty<string>();
    }
}
