using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Scheduling;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 205 slice 4 fix 1: a converted route guard decides about the caller's GRANT SUBJECT, on both
/// planes, over a real gate and real grants.
/// </summary>
/// <remarks>
/// The slice shipped asking the gate about the request's canonical PARTY. A party holds no grant, so every
/// signed-in web member was refused at all thirty-six converted sites while the desktop plane stayed green
/// (its party id and its principal id are the same string). The fixture below is built so that cannot hide
/// again: each web caller's party is deliberately a DIFFERENT value from that caller's principal, so a
/// guard that names the wrong one refuses a caller who holds the operation.
/// </remarks>
public sealed class WebPlaneGrantSubjectRouteTests : IAsyncLifetime
{
    private const string CallerToken = "s205s4-grant-subject-caller-token";
    private const string MemberHandle = "s205s4-member-selected-handle";
    private const string StrangerHandle = "s205s4-stranger-selected-handle";
    private const string MemberPrincipal = "s205s4-member-principal";
    private const string StrangerPrincipal = "s205s4-stranger-principal";

    // The two web callers' PARTY ids - deliberately not their principal ids.
    private const string MemberParty = "party:s205s4-member";
    private const string StrangerParty = "party:s205s4-stranger";

    private static readonly TeamId Team = new(new Guid("20500000-0000-0000-0000-0000000004a1"));
    private static readonly DateTimeOffset At = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "s205s4-grant-subject-" + Guid.NewGuid().ToString("N") + ".db");
    private ServiceProvider _grants = null!;
    private ServiceProvider _outer = null!;
    private SharedHostedWebApp _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var teams = new TeamContextFactory(TimeProvider.System);
        await teams.GetOrCreateAsync(Team, "Grant subject team", CancellationToken.None);
        var active = new ActiveTeamAccessor(teams);
        await active.SetActiveAsync(Team, CancellationToken.None);

        // The REAL seed over the REAL gate: the desktop operator's node-operator grant comes from here.
        var grantServices = new ServiceCollection();
        grantServices.AddAccessGrantModule();
        _grants = grantServices.BuildServiceProvider();
        await new AuthorizationSeedHostedService(
            _grants.GetRequiredService<AccessGrantAuthorizationSeed>(),
            active,
            teams,
            AuthorizationSeedProfile.Production,
            new FixedClock(At)).StartAsync(CancellationToken.None);

        // The invited member's own live grant, issued to the PRINCIPAL - the key the grant store and the
        // closure reader use, exactly as InitialGrantIssuanceService issues it.
        var tenant = NodeTenant.Resolve(active);
        await _grants.GetRequiredService<IGrantStore>().AppendAsync(tenant, new AccessGrant(
            GrantId.New(), tenant, new ActorId(MemberPrincipal), AccessGrantAuthorizationSeed.MemberRole,
            ScopeExpression.Parse("/"), GrantResidency.Cache, new GrantValidity(At.AddHours(-1)),
            GranterKind.Person, new ActorId("s205s4-granter"), At.AddHours(-1),
            new GrantProvenance(
                GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), new ActorId("s205s4-granter")),
            At.AddHours(-1)));

        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging(logging => logging.ClearProviders());
        outer.AddSingleton<IActiveTeamAccessor>(active);
        outer.AddSingleton(_grants.GetRequiredService<AuthorizationGate>());
        outer.AddSingleton<ISelectedSessionPermissionResolver>(new AlwaysAdmittingPermissionResolver());
        outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
        outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new TwoMemberSelectedSessionAuthority(tenant));
        outer.AddDbContextFactory<NodeLocalSchedulingDbContext>(options => options.UseSqlite(
            $"Data Source={_dbPath};Pooling=False"));
        _outer = outer.BuildServiceProvider();

        var factory = _outer.GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();

        _app = new SharedHostedWebApp(
            _outer,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            _outer.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            _outer.GetRequiredService<TimeProvider>());
        _app.MapApiRoutes(routes => SchedulingDefinitionRoutes.Map(
            routes.MapDeviceReachableProductDataGroup(),
            new NodeSchedulingDraftStore(factory, TimeProvider.System),
            new SchedulingDraftValidator(),
            parties: null!,
            _outer.GetRequiredService<IActiveTeamAccessor>(),
            new FixedCurrentUser(MemberPrincipal),
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
    public async Task A_web_plane_member_holding_the_operation_reaches_the_converted_route()
    {
        using var request = WebRequest(MemberHandle);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_web_plane_caller_without_a_grant_is_refused_by_the_converted_route()
    {
        using var request = WebRequest(StrangerHandle);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_desktop_founder_reaches_the_converted_route()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SchedulingDefinitionRoutes.RouteBase);
        request.Headers.TryAddWithoutValidation(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
        if (_outer is not null) await _outer.DisposeAsync();
        if (_grants is not null) await _grants.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static HttpRequestMessage WebRequest(string handle)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, SchedulingDefinitionRoutes.RouteBase);
        request.Headers.TryAddWithoutValidation("Cookie", $"{WebSessionCookieNames.Selected}={handle}");
        return request;
    }

    /// <summary>
    /// Both web callers are admitted by the per-request PEP with the operation in hand. The refusal under
    /// test must therefore come from the GATE reading the caller's grants - not from the session's
    /// permission set, which is the same for both.
    /// </summary>
    private sealed class AlwaysAdmittingPermissionResolver : ISelectedSessionPermissionResolver
    {
        public ValueTask<PermissionSet?> ResolveAsync(
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PermissionSet?>(PermissionSet.Of(Permission.SchedulingRead));
    }

    private sealed class TwoMemberSelectedSessionAuthority(TenantId tenant) : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(selectedHandle switch
            {
                MemberHandle => Principal(MemberPrincipal, MemberParty),
                StrangerHandle => Principal(StrangerPrincipal, StrangerParty),
                _ => null,
            });

        private SelectedSessionRequestPrincipal? Principal(string principal, string party) =>
            new(
                principal + "-account",
                tenant,
                new PrincipalUserId(principal),
                new CanonicalPartyReference(party),
                principal + "-membership",
                1,
                [new PinnedGrantOwnerVersion(principal + "-grant", 1)],
                1,
                principal + "-session",
                principal + "-coordination");
    }

    private sealed class FixedCurrentUser(string userId) : ICurrentUser
    {
        public string UserId { get; } = userId;
        public IReadOnlyList<string> Roles { get; } = Array.Empty<string>();
    }

    private sealed class FixedClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
