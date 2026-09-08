using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Route-level tests for the node-local teams chrome surface
/// (<see cref="TeamRoutes"/>) — the joined-team list + active-team reads the
/// Harborline App shell consumes. Hosts the SAME route handler
/// <see cref="HostedTeamsApiEndpoint"/> registers, on a real in-process Kestrel
/// listener, driven with a real <see cref="HttpClient"/> to pin the wire contract
/// FED's <c>teamsClient.ts</c> consumes (<c>src/teams/types.ts</c>).
/// </summary>
/// <remarks>
/// The joined-team list is the materialized <see cref="ITeamContextFactory.Active"/>
/// snapshot, so the test wires a <see cref="FakeTeamContextFactory"/> over two real
/// <see cref="TeamContext"/> instances + a <see cref="FakeActiveTeamAccessor"/>
/// + a real <see cref="InMemoryTeamRegistry"/> seeded with memberships for the
/// member count. <see cref="TeamRoutes.Map"/> is the production registration helper
/// (single source of truth, no test/prod drift).
/// </remarks>
public sealed class TeamRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly TeamId TeamB = new(new Guid("22222222-2222-2222-2222-222222222222"));

    private const string TeamADisplay = "Team 11111111-1111-1111-1111-111111111111";
    private const string TeamBDisplay = "Team 22222222-2222-2222-2222-222222222222";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        var teamA = MakeTeam(TeamA, TeamADisplay);
        var teamB = MakeTeam(TeamB, TeamBDisplay);
        var factory = new FakeTeamContextFactory(teamA, teamB);
        var accessor = new FakeActiveTeamAccessor(teamA); // A is active

        var memberships = new InMemoryTeamRegistry();
        // Team A: two members (operator + one admitted peer). Team B: one member.
        await Seed(memberships, new ActorId("operator"), TeamA, TeamADisplay);
        await Seed(memberships, new ActorId("peer"), TeamA, TeamADisplay);
        await Seed(memberships, new ActorId("operator"), TeamB, TeamBDisplay);

        // Map the SAME production routes the hosted endpoint registers.
        TeamRoutes.Map(_app, factory, accessor, memberships);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task GET_active_team_returns_the_ActiveTeam_contract_shape()
    {
        var response = await _client.GetAsync(TeamRoutes.ActiveRouteBase);
        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(TeamA.Value.ToString(), root.GetProperty("teamId").GetString());
        Assert.Equal(TeamADisplay, root.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GET_teams_returns_the_joined_teams_with_isActive_and_memberCount()
    {
        var response = await _client.GetAsync(TeamRoutes.ListRouteBase);
        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var teams = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, teams.Count);

        // Each entry carries the full TeamSummary contract shape.
        foreach (var team in teams)
        {
            Assert.True(team.TryGetProperty("teamId", out _));
            Assert.True(team.TryGetProperty("name", out _));
            Assert.True(team.TryGetProperty("isActive", out _));
            Assert.True(team.TryGetProperty("memberCount", out _));
        }

        var a = Assert.Single(teams, t => t.GetProperty("teamId").GetString() == TeamA.Value.ToString());
        Assert.Equal(TeamADisplay, a.GetProperty("name").GetString());
        Assert.True(a.GetProperty("isActive").GetBoolean());          // A is the active team
        Assert.Equal(2, a.GetProperty("memberCount").GetInt32());     // operator + peer

        var b = Assert.Single(teams, t => t.GetProperty("teamId").GetString() == TeamB.Value.ToString());
        Assert.Equal(TeamBDisplay, b.GetProperty("name").GetString());
        Assert.False(b.GetProperty("isActive").GetBoolean());         // B is NOT active
        Assert.Equal(1, b.GetProperty("memberCount").GetInt32());     // operator only

        // Exactly one team is active.
        Assert.Single(teams, t => t.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task GET_active_team_returns_204_when_no_team_is_active()
    {
        // A fresh host with NO active team yet (boot) and no joined teams.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();

        TeamRoutes.Map(app, new FakeTeamContextFactory(), new FakeActiveTeamAccessor(null),
            new InMemoryTeamRegistry());

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

        var active = await client.GetAsync(TeamRoutes.ActiveRouteBase);
        Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);

        var list = await client.GetAsync(TeamRoutes.ListRouteBase);
        Assert.True(list.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.EnumerateArray());

        await app.StopAsync();
    }

    [Fact]
    public async Task Both_routes_reject_401_when_caller_auth_is_enforced_and_no_bearer_is_presented()
    {
        const string token = "the-per-boot-session-token";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();

        var team = MakeTeam(TeamA, TeamADisplay);
        // The per-route check is mapped with an ENFORCING token (defence-in-depth;
        // the authoritative gate is the SharedHostedWebApp listener middleware).
        TeamRoutes.Map(app, new FakeTeamContextFactory(team), new FakeActiveTeamAccessor(team),
            new InMemoryTeamRegistry(), new NodeCallerSessionToken(token));

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

        // No bearer → fail-closed 401 on BOTH routes.
        var listNoAuth = await client.GetAsync(TeamRoutes.ListRouteBase);
        Assert.Equal(HttpStatusCode.Unauthorized, listNoAuth.StatusCode);

        var activeNoAuth = await client.GetAsync(TeamRoutes.ActiveRouteBase);
        Assert.Equal(HttpStatusCode.Unauthorized, activeNoAuth.StatusCode);

        // Wrong bearer → still 401.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var listWrong = await client.GetAsync(TeamRoutes.ListRouteBase);
        Assert.Equal(HttpStatusCode.Unauthorized, listWrong.StatusCode);

        // Correct bearer → 200.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listOk = await client.GetAsync(TeamRoutes.ListRouteBase);
        Assert.Equal(HttpStatusCode.OK, listOk.StatusCode);

        var activeOk = await client.GetAsync(TeamRoutes.ActiveRouteBase);
        Assert.Equal(HttpStatusCode.OK, activeOk.StatusCode);

        await app.StopAsync();
    }

    private static TeamContext MakeTeam(TeamId id, string displayName)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TeamContext(id, displayName, services, TimeProvider.System);
    }

    private static ValueTask Seed(
        IMutableTeamRegistry memberships, ActorId actor, TeamId team, string displayName)
    {
        var membership = new TeamMembership(
            TeamId: team.Value,
            DisplayName: displayName,
            RoleDisplayName: "Member",
            SubkeyFingerprint: KeyFingerprint.FromPublicKey(team.Value.ToByteArray()),
            Role: TeamRole.Member,
            Permissions: PermissionCompositions.ForRole(TeamRole.Member));
        return memberships.AddMembershipAsync(actor, membership);
    }

    private sealed class FakeActiveTeamAccessor : IActiveTeamAccessor
    {
        public FakeActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    private sealed class FakeTeamContextFactory : ITeamContextFactory
    {
        private readonly IReadOnlyCollection<TeamContext> _teams;
        public FakeTeamContextFactory(params TeamContext[] teams) => _teams = teams;
        public IReadOnlyCollection<TeamContext> Active => _teams;
        public Task<TeamContext> GetOrCreateAsync(TeamId teamId, string displayName, CancellationToken ct)
            => Task.FromResult(_teams.First(t => t.TeamId.Equals(teamId)));
        public Task RemoveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
    }
}
