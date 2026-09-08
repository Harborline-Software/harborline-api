using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local audit-events surface — the T4 audit system-of-record node-flip (ADR 0126). Serves the
/// audit-trail viewer (<c>AuditEventsPage</c> / <c>AuditEventDetailPage</c>) from the SC-4-recoverable
/// <c>node_audit_events</c> table in <c>local-node.db</c>, replacing the Bridge
/// <c>/api/v1/audit-events</c> dependency for a single-device install.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes (mirror the Bridge wire contract</b> <c>audit-events.ts</c> consumes<b>):</b>
/// <list type="bullet">
///   <item><c>GET /api/local-node/audit-events</c> — paginated reverse-chronological list. Filters:
///     <c>?from=</c>/<c>?to=</c> (ISO), <c>?event_type=</c>, <c>?correlation_id=</c>,
///     <c>?severity=</c> (coarse channel prefix), <c>?page_size=</c>, opaque <c>?cursor=</c>. Response
///     <c>{ events, next_cursor, has_more }</c>.</item>
///   <item><c>GET /api/local-node/audit-events/{auditId}</c> — single record detail; opaque 404 if
///     absent or other-tenant.</item>
/// </list>
/// No CSV export route for v1 (the frontend list/detail are the page consumers; export is a Bridge-era
/// surface the node viewer does not yet wire — a tracked follow-on, not an acceptance item).
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091/0092).</b> Every read resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer),
/// server-set, never frontend-passed, not a fixed <c>"local"</c> sentinel. The reader applies a
/// <c>WHERE TenantId</c> — the per-org isolation predicate. A caller-supplied <c>tenant_id</c> query param
/// is rejected 400 (parity with the Bridge Decision-7 posture; no TBV emission needed on the loopback
/// node).
/// </para>
/// <para>
/// <b>Offline integrity.</b> <c>signature_state</c> is computed by the reader via the offline,
/// key-independent <c>HashChain</c> verdict + sealed pre-reseed epoch logic (ADR 0126 §D4) — it works
/// fully offline and survives a passphrase reseed; a retired-epoch signed record NEVER reads
/// <c>VerificationFailed</c>.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only listener, same posture as every <c>/api/local-node/*</c>
/// route. <b>Wiring (bug-2849):</b> the reader is injected from the OUTER host container and passed to
/// <see cref="Map"/> as a closed-over dependency, not resolved via <c>[FromServices]</c> on the inner
/// shared-app container.
/// </para>
/// </remarks>
public static class AuditEventRoutes
{
    /// <summary>Canonical route base for the node-local audit-events surface.</summary>
    public const string RouteBase = "/api/local-node/audit-events";


    /// <summary>Default page size when the caller omits <c>page_size</c>.</summary>
    public const int DefaultPageSize = NodeAuditEventReader.DefaultPageSize;

    /// <summary>Hard upper bound on caller-requested page size.</summary>
    public const int MaxPageSize = NodeAuditEventReader.MaxPageSize;

    /// <summary>Coarse-grain severity-channel allowlist (parity with the Bridge endpoint).</summary>
    private static readonly HashSet<string> SeverityAllowlist =
        new(StringComparer.Ordinal) { "Security", "Financial", "Messaging", "Authentication", "Maintenance" };

    /// <summary>
    /// Maps the audit-event routes onto <paramref name="app"/>, closing over <paramref name="reader"/>
    /// from the outer host container.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, NodeAuditEventReader reader, IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(activeTeam);

        MapList(app, reader, activeTeam);
        MapDetail(app, reader, activeTeam);
    }

    // ── GET /api/local-node/audit-events ──────────────────────────────────────
    private static void MapList(IEndpointRouteBuilder app, NodeAuditEventReader reader, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (
            HttpRequest request,
            string? from,
            string? to,
            string? event_type,
            string? correlation_id,
            string? severity,
            int? page_size,
            string? cursor,
            HttpContext http,
            CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            // Ticket 217 / L628: the audit trail is read through `audit:read` at the point of use, and the
            // Auditor holds nothing else. The LIST addresses the install's trail rather than one entry.
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.AuditRead, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            var LocalTenantId = tenant.Value;
            // Decision-7 parity — tenant is server-derived; reject a caller-supplied tenant_id.
            if (request.Query.ContainsKey("tenant_id"))
            {
                return Results.BadRequest(new { title = "tenant_id_not_caller_supplied" });
            }

            if (!string.IsNullOrEmpty(severity) && !SeverityAllowlist.Contains(severity))
            {
                return Results.BadRequest(new { title = "invalid_severity" });
            }

            DateTimeOffset? parsedFrom = null, parsedTo = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!DateTimeOffset.TryParse(from, out var f))
                {
                    return Results.BadRequest(new { title = "invalid_from" });
                }
                parsedFrom = f;
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!DateTimeOffset.TryParse(to, out var t))
                {
                    return Results.BadRequest(new { title = "invalid_to" });
                }
                parsedTo = t;
            }
            if (parsedFrom is { } pf && parsedTo is { } pt && pf > pt)
            {
                return Results.BadRequest(new { title = "inverted_range" });
            }

            var size = page_size ?? DefaultPageSize;
            if (size <= 0 || size > MaxPageSize)
            {
                return Results.BadRequest(new { title = "invalid_page_size" });
            }

            NodeAuditCursor? decodedCursor = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                decodedCursor = NodeAuditEventReader.TryDecodeCursor(cursor);
                if (decodedCursor is null)
                {
                    return Results.BadRequest(new { title = "invalid_cursor" });
                }
                if (!string.Equals(decodedCursor.TenantId, LocalTenantId, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { title = "tenant_changed_reload_page" });
                }
            }

            var query = new NodeAuditEventReaderQuery(
                EventType: string.IsNullOrWhiteSpace(event_type) ? null : event_type,
                From: parsedFrom,
                To: parsedTo,
                CorrelationId: string.IsNullOrWhiteSpace(correlation_id) ? null : correlation_id,
                PageSize: size,
                Cursor: decodedCursor);

            var page = await reader.ListAsync(LocalTenantId, query, ct).ConfigureAwait(false);

            // Severity is a coarse channel prefix mapped to event_type StartsWith(severity + ".").
            var prefix = string.IsNullOrEmpty(severity) ? null : severity + ".";
            var events = page.Events
                .Where(v => prefix is null || v.EventType.StartsWith(prefix, StringComparison.Ordinal))
                .Select(ToWire)
                .ToList();

            return Results.Ok(new
            {
                events,
                next_cursor = page.NextCursor,
                has_more = page.HasMore,
            });
        });
    }

    // ── GET /api/local-node/audit-events/{auditId} ────────────────────────────
    private static void MapDetail(IEndpointRouteBuilder app, NodeAuditEventReader reader, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{auditId}}", async (
            string auditId,
            HttpRequest request,
            HttpContext http,
            CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            // The DETAIL addresses one audit entry, so it passes that entry as its record target: a grant
            // scoped to another entry refuses here rather than reading through.
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.AuditRead, RouteRecord.Of(auditId), ct) is { } denied)
                return denied;
            var LocalTenantId = tenant.Value;
            if (request.Query.ContainsKey("tenant_id"))
            {
                return Results.BadRequest(new { title = "tenant_id_not_caller_supplied" });
            }
            if (string.IsNullOrWhiteSpace(auditId))
            {
                return Results.BadRequest(new { title = "invalid_audit_id" });
            }

            var view = await reader.GetByIdAsync(LocalTenantId, auditId, ct).ConfigureAwait(false);
            return view is null ? Results.NotFound() : Results.Ok(ToWire(view));
        });
    }

    /// <summary>
    /// Projects a <see cref="NodeAuditEventView"/> to the snake_case wire object the frontend
    /// <c>AuditEventSummary</c> / <c>AuditEventDetail</c> deserialise (audit-events.ts).
    /// </summary>
    private static object ToWire(NodeAuditEventView v) => new
    {
        audit_id = v.AuditId,
        occurred_at = v.OccurredAt,
        event_type = v.EventType,
        actor = v.Actor,
        correlation_id = v.CorrelationId,
        tenant_id = v.TenantId,
        payload_summary = v.PayloadSummary,
        signature_state = v.SignatureState,
    };
}
