using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local teams chrome surface — the joined-team list + the current active
/// team the Harborline App shell reads (ADR 0032 active-team concept). Exposes the two
/// reads FED's <c>teamsClient.ts</c> is built against so the Harborline App renders the
/// REAL teams instead of the mock Alpha/Beta fallback.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes (READ-ONLY):</b>
/// <list type="bullet">
///   <item><description><c>GET /api/local-node/teams</c> → the joined-team list
///     (<c>TeamSummary[]</c>: <c>{ teamId, name, isActive, memberCount }</c>),
///     projected from the materialized contexts the node has joined
///     (<see cref="ITeamContextFactory.Active"/>) cross-referenced with the
///     active team (<see cref="IActiveTeamAccessor.Active"/>) and the membership
///     roster (<see cref="IMutableTeamRegistry.GetRosterAsync"/> per team).</description></item>
///   <item><description><c>GET /api/local-node/teams/active</c> → the current
///     active team (<c>ActiveTeam</c>: <c>{ teamId, name }</c>) from
///     <see cref="IActiveTeamAccessor.Active"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Source of the joined-team list.</b> Unlike <see cref="IActiveTeamAccessor"/>
/// (which exposes only the CURRENT team), the joined set is the materialized
/// <see cref="ITeamContextFactory.Active"/> snapshot — every team the node has
/// joined + opened a store for. Each context carries its
/// <see cref="TeamContext.TeamId"/> + <see cref="TeamContext.DisplayName"/>.
/// </para>
/// <para>
/// <b>Team NAME honesty (PRODUCT GAP — flagged).</b> No human-friendly team name is
/// persisted on the Harborline App (genesis/single-office) path. The
/// <see cref="MultiTeamBootstrapHostedService"/> legacy/genesis branch sets
/// <see cref="TeamContext.DisplayName"/> to the derived label
/// <c>"Team {teamId:D}"</c> (the team's GUID). So this route returns that
/// derived label as <c>name</c> — it is the truthful identifier, NOT an invented
/// "Alpha"/"Beta". When the Harborline App create-team flow captures + persists a real
/// display name (a follow-up — there is no team-naming surface today), it flows
/// through <see cref="TeamContext.DisplayName"/> here with no route change.
/// </para>
/// <para>
/// <b>memberCount.</b> The per-team roster size from the org-first membership view
/// (<see cref="IMutableTeamRegistry.GetRosterAsync"/>). On a fresh single-office
/// node the operator is enrolled as the sole Admin (count = 1); it grows as peers
/// are admitted.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1).</b> Like every other non-allowlisted
/// <c>/api/local-node/*</c> route these are gated by the LISTENER-LEVEL caller-auth
/// middleware (<c>SharedHostedWebApp</c>): a loopback bind authenticates the host,
/// not the calling process, so the Harborline App's per-boot session token is required
/// (fail-closed 401) when configured. Each route ALSO keeps a per-route check as
/// defence-in-depth (mirrors <see cref="SyncStatusRoutes"/>). Both routes are
/// pure projections — no mutation.
/// </para>
/// </remarks>
public static class TeamRoutes
{
    /// <summary>Canonical route for the joined-team list.</summary>
    public const string ListRouteBase = "/api/local-node/teams";

    /// <summary>Canonical route for the current active team.</summary>
    public const string ActiveRouteBase = "/api/local-node/teams/active";

    /// <summary>
    /// Maps both team routes onto <paramref name="app"/>, closing over the
    /// install-level <paramref name="factory"/> (joined-team source),
    /// <paramref name="activeTeam"/> (active-team source), and
    /// <paramref name="memberships"/> (per-team roster for the member count).
    /// Caller-auth runs un-enforced (the dev/single-host fallback).
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ITeamContextFactory factory,
        IActiveTeamAccessor activeTeam,
        IMutableTeamRegistry memberships)
        => Map(app, factory, activeTeam, memberships, new NodeCallerSessionToken(null));

    /// <summary>
    /// Maps both team routes with the inc-4 cross-process CALLER-AUTH guard
    /// (<paramref name="callerAuth"/>) enforced. A local stranger process without the
    /// Harborline App's per-boot session token is rejected <b>fail-closed (401)</b> before any
    /// team data is projected; a missing token (dev/single-host) runs un-enforced (the
    /// 4-arg overload above). See <see cref="NodeCallerSessionToken"/>.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ITeamContextFactory factory,
        IActiveTeamAccessor activeTeam,
        IMutableTeamRegistry memberships,
        NodeCallerSessionToken callerAuth)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(callerAuth);

        // GET /api/local-node/teams/active → ActiveTeam { teamId, name }
        app.MapGet(ActiveRouteBase, (HttpRequest httpRequest) =>
        {
            // inc-4 CALLER AUTH (fail-closed): loopback bind ≠ caller trust.
            // The AUTHORITATIVE gate is the LISTENER-LEVEL middleware (SharedHostedWebApp,
            // gate-all-by-default); this per-route check is defence-in-depth.
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
                return NodeCallerSessionToken.RejectResult();

            var active = activeTeam.Active;
            if (active is null)
            {
                // No team active yet (boot). A node with no materialized team has no
                // active team — return 204 so the Harborline App holds its current selection
                // rather than mis-rendering an empty one.
                return Results.NoContent();
            }

            return Results.Ok(ActiveTeamResponse.From(active));
        });

        // GET /api/local-node/teams → TeamSummary[]
        app.MapGet(ListRouteBase, async (HttpRequest httpRequest, CancellationToken ct) =>
        {
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
                return NodeCallerSessionToken.RejectResult();

            var activeId = activeTeam.Active?.TeamId;

            var summaries = new List<TeamSummaryResponse>();
            foreach (var team in factory.Active)
            {
                var roster = await memberships.GetRosterAsync(team.TeamId.Value, ct).ConfigureAwait(false);
                summaries.Add(TeamSummaryResponse.From(
                    team,
                    isActive: activeId is { } a && a.Equals(team.TeamId),
                    memberCount: roster.Count));
            }

            return Results.Ok(summaries);
        });
    }
}

// ── Wire shapes — the Harborline App teams contract (camelCase JSON) ───────────────────

/// <summary>
/// The <c>GET /api/local-node/teams/active</c> response. Matches the Harborline App
/// <c>ActiveTeam</c> contract (<c>src/teams/types.ts</c>).
/// </summary>
/// <param name="TeamId">The active team's id (GUID string, lowercase).</param>
/// <param name="Name">The team display name (the derived <c>"Team {guid}"</c>
/// label today — see <see cref="TeamRoutes"/> NAME honesty remark).</param>
public sealed record ActiveTeamResponse(
    [property: JsonPropertyName("teamId")] string TeamId,
    [property: JsonPropertyName("name")] string Name)
{
    /// <summary>Projects a <see cref="TeamContext"/> onto the active-team wire shape.</summary>
    public static ActiveTeamResponse From(TeamContext team) => new(
        TeamId: team.TeamId.Value.ToString(),
        Name: team.DisplayName);
}

/// <summary>
/// One entry in the <c>GET /api/local-node/teams</c> response. Matches the Harborline App
/// <c>TeamSummary</c> contract (<c>src/teams/types.ts</c>).
/// </summary>
/// <param name="TeamId">The team's id (GUID string, lowercase).</param>
/// <param name="Name">The team display name (the derived label today).</param>
/// <param name="IsActive">Whether this is the currently active team.</param>
/// <param name="MemberCount">The roster size (org-first membership view).</param>
public sealed record TeamSummaryResponse(
    [property: JsonPropertyName("teamId")] string TeamId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("memberCount")] int MemberCount)
{
    /// <summary>Projects a <see cref="TeamContext"/> onto the team-summary wire shape.</summary>
    public static TeamSummaryResponse From(TeamContext team, bool isActive, int memberCount) => new(
        TeamId: team.TeamId.Value.ToString(),
        Name: team.DisplayName,
        IsActive: isActive,
        MemberCount: memberCount);
}
