using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Governed metadata enumeration of the kernel trail, never submitted bodies or diagnostics.</summary>
internal static class KernelAuditMetadataRoutes
{
    internal const string Route = "/api/session/audit/metadata";
    private static readonly string[] IdentifierKeys =
        ["entity_id", "grant_id", "successor_grant_id", "workflow_instance_id", "packKey", "version"];

    internal static void Map(IEndpointRouteBuilder app, IAuditTrail trail, TimeProvider time) =>
        app.MapGet(Route, (HttpContext http, DateTimeOffset from, DateTimeOffset to,
            Guid? after, int? limit, CancellationToken ct) => ReadAsync(http, trail, time, from, to, after, limit ?? 100, ct));

    internal static async Task<IResult> ReadAsync(HttpContext http, IAuditTrail trail, TimeProvider time,
        DateTimeOffset from, DateTimeOffset to, Guid? after, int limit, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var selected = http.Features.Get<SelectedSessionRequestPrincipal>();
        if (selected is null || selected.TenantId.IsSystemSentinel ||
            string.IsNullOrWhiteSpace(http.Request.Cookies[WebSessionCookieNames.Selected])) return Results.Unauthorized();
        var authority = RequestAuthorization.Authority(http, selected.TenantId, time);
        if (await RequestAuthorization.RefusalAsync(http, authority, Permission.AuditRead, RouteRecord.TheInstall, ct)
            .ConfigureAwait(false) is { } denial) return denial;
        if (from > to || to - from > TimeSpan.FromDays(1) || limit is < 1 or > 1000 || after == Guid.Empty)
            return Results.BadRequest(new { code = "audit.metadata.invalid_range_or_cursor" });
        var found = after is null;
        var rows = new List<Metadata>();
        var more = false;
        await foreach (var row in trail.QueryAsync(new AuditQuery(selected.TenantId,
            OccurredAfter: from, OccurredBefore: to), ct).ConfigureAwait(false))
        {
            if (!found)
            {
                found = row.AuditId == after;
                continue;
            }
            if (rows.Count == limit) { more = true; break; }
            rows.Add(Project(row));
        }
        if (!found) return Results.BadRequest(new { code = "audit.metadata.cursor_not_found" });
        return Results.Ok(new { rows, nextCursor = more ? rows[^1].AuditId : (Guid?)null });
    }

    private static Metadata Project(AuditRecord row)
    {
        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in IdentifierKeys)
            if (ReadText(row, key) is { } value) identifiers.Add(key, value);
        var correlation = Guid.TryParse(ReadText(row, "correlation_id"), out var parsed) ? parsed : (Guid?)null;
        return new(row.AuditId, row.EventType.Value, row.OccurredAt, row.TenantId.Value, row.Actor?.Value,
            row.Target is { } target ? new(target.RecordKind, target.RecordId, target.Scope.ToString()) : null,
            row.Act is { } act ? new(act.Operation.Value, act.Scope.ToString()) : null, correlation, identifiers);
    }

    private static string? ReadText(AuditRecord row, string key) =>
        row.Payload.Payload.Body.TryGetValue(key, out var value) ? value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            Guid guid => guid.ToString("D"),
            _ => null,
        } : null;

    private sealed record Target(string Kind, string Id, string Scope);
    private sealed record Act(string Operation, string Scope);
    private sealed record Metadata(Guid AuditId, string EventType, DateTimeOffset OccurredAt, string TenantId,
        string? Actor, Target? Target, Act? Act, Guid? CorrelationId, IReadOnlyDictionary<string, string> Identifiers);
}
