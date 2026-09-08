using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local clipped KEYWORD (FTS5) knowledge-graph search surface (Harborline App KG keyword-search demo, ONR
/// survey <c>onr-carrier-kg-search-calendar-demo-survey-2026-06-24</c>). The FIRST production caller of the
/// otherwise-built-but-unwired KG read substrate:
/// <list type="bullet">
///   <item><b>Search</b> — <c>GET /api/local-node/kg/search?q=&lt;term&gt;&amp;limit=&lt;n&gt;</c> returns
///     the records the acting principal is AUTHORIZED to see whose indexed text matches the query
///     (trigram FTS5; CJK-capable), each as a clipped hit (title / node-type / record-id / body snippet).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the clip, never around it (the load-bearing security property).</b> Every read goes THROUGH
/// <see cref="NodeSearchReadService.SearchAsync"/> — the clipped path. That service resolves the
/// fail-closed <see cref="AuthorizedRecordScope"/> FIRST (from the principal's active, cache-resident
/// grants) and builds the query <c>WHERE</c> EXCLUSIVELY from it; a principal with no grant short-circuits
/// to an empty result WITHOUT touching the index. This route NEVER issues a raw <c>search_nodes</c> read —
/// <c>SearchClipArchFence</c> makes that structurally impossible, and a "demo bypass" raw read would be the
/// no-mock-crypto anti-pattern (a dev route silently becoming load-bearing for confidentiality). The route
/// is a thin shell over the clipped service; the clip is the substrate's, not this route's.
/// </para>
/// <para>
/// <b>Principal + tenant resolved SERVER-SIDE, with the web plane fail-closed.</b> A selected-session
/// request returns the route's normal empty clip before it can consume either desktop authority source
/// (ADR 0160 R3-D). Resolving the member's real read scope is MTW-3; this MTW-2 fence only stops inheritance.
/// On the desktop plane, the tenant comes from the active-team (<c>NodeTenant.Resolve</c>) — the Harborline App
/// sends no tenant. The acting principal comes from
/// <see cref="CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal"/> (<c>os:&lt;user&gt;</c>) — NOT a
/// caller-supplied query param. This is the SAME helper the dev grant seed
/// (<see cref="KgCalendarDevIndexer"/>) uses to key the grant, so the route's <see cref="ActorId"/> and the
/// grant's <c>PrincipalId</c> are the IDENTICAL string (the pinned footgun: a divergence would drop every
/// row). The clip's <c>at</c> snapshot comes from the composition-root clock.
/// </para>
/// <para>
/// <b>No existence leak.</b> A missing / blank <c>q</c> is a uniform 400; an empty result (no match, or no
/// grant) is a normal <c>200 { "data": [] }</c> — the same shape whether the principal is unauthorized or
/// there simply is no match, so the response never distinguishes "forbidden" from "absent".
/// </para>
/// <para>
/// <b>Closed-over deps</b>, NOT <c>[FromServices]</c> — the routes mount on <c>SharedHostedWebApp</c>'s
/// inner <c>WebApplication</c> whose provider lacks the outer registrations (bug-2849). The hosted endpoint
/// resolves the read service + active-team accessor from the OUTER container and passes them into
/// <see cref="Map"/>.
/// </para>
/// </remarks>
public static class KgSearchRoutes
{
    /// <summary>Canonical route base for the node-local KG keyword-search surface.</summary>
    public const string RouteBase = "/api/local-node/kg";

    /// <summary>
    /// Maps <c>GET <see cref="RouteBase"/>/search</c> onto <paramref name="app"/>, closing over the clipped
    /// read service + the active-team accessor. The single source of truth for the wire contract (shared
    /// with the route tests so there is no test/prod drift).
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        NodeSearchReadService readService,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(readService);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(timeProvider);

        app.MapGet($"{RouteBase}/search", async (
            string? q,
            int? limit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_query",
                    detail = "A non-empty 'q' query param is required.",
                });
            }

            // ADR 0160 R3-D / card #3384 — a hosted-web member cannot consume the desktop active-team
            // tenant or the os:<user> principal. Their real read scope is MTW-3 and does not exist yet,
            // so use search's already-defined fail-closed shape: an indistinguishable empty 200 result.
            // The SAME ambient plane signal drives the authorization and route-family fences; adding a
            // second ambient context holder would let the safety controls disagree about which plane is acting.
            if (NodeCallerAttributionScope.HasBoundWebPrincipal)
            {
                return Results.Ok(new KgSearchResponse(Array.Empty<KgSearchHitWire>()));
            }

            var tenantId = NodeTenant.Resolve(activeTeam);

            // DESKTOP plane only: resolve the acting principal server-side (not caller-supplied) — the
            // same os:<user> form the dev grant seed keys on, so the clip authorizes the seeded records.
            var principalId = new ActorId(CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal().Id);

            var effectiveLimit = limit ?? NodeSearchReadService.DefaultLimit;

            // THROUGH the clipped read service — the clip resolves the authorized scope first and builds the
            // WHERE from it. A principal with no grant returns an empty list (fail-closed), never a leak.
            var hits = await readService
                .SearchAsync(tenantId, principalId, q, timeProvider.GetUtcNow(), effectiveLimit, ct)
                .ConfigureAwait(false);

            var wire = hits.Select(KgSearchHitWire.From).ToList();
            return Results.Ok(new KgSearchResponse(wire));
        });
    }
}

// ── Wire shapes (camelCase JSON) ──────────────────────────────────────────────────────────────────

/// <summary>One clipped KG search hit — the search-facing projection of an authorized node.</summary>
public sealed record KgSearchHitWire(
    [property: JsonPropertyName("recordId")] string RecordId,
    [property: JsonPropertyName("nodeType")] string NodeType,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("snippet")] string Snippet)
{
    /// <summary>Max body characters carried as the snippet (the clipped hit's preview text).</summary>
    private const int SnippetMaxChars = 240;

    /// <summary>Projects a clipped <see cref="SearchHit"/> onto the wire shape (the body is a trimmed snippet).</summary>
    public static KgSearchHitWire From(SearchHit h) => new(
        RecordId: h.RecordId,
        NodeType: h.NodeType,
        Title: h.Title,
        Snippet: BuildSnippet(h.Body));

    private static string BuildSnippet(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var trimmed = body.Trim();
        return trimmed.Length <= SnippetMaxChars ? trimmed : trimmed[..SnippetMaxChars] + "…";
    }
}

/// <summary>The KG search response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record KgSearchResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<KgSearchHitWire> Data);
