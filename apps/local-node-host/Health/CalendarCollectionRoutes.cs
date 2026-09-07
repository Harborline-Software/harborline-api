using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Calendar;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local owned-CALENDAR surface (calendar productization #149, slice C1) — the calendar-collection
/// list + create endpoints that fill the <c>CalendarId</c> seam:
/// <list type="bullet">
///   <item><b>List</b> — <c>GET /api/local-node/calendar/calendars</c> returns the tenant's owned
///     calendars (the default first).</item>
///   <item><b>Create</b> — <c>POST /api/local-node/calendar/calendars</c> creates a new owned calendar
///     (name + kind + optional colour token + optional resource ref) and returns it.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Write surface — per-route caller-auth from day one (CIC 2026-07-07; #138 / PR-1851 posture).</b>
/// The authoritative gate is the LISTENER-LEVEL caller-auth middleware
/// (<see cref="SharedHostedWebApp"/>) that rejects every non-allowlisted node route without the
/// Harborline App's per-boot session token (fail-closed 401). These routes are NOT allowlisted, so they inherit
/// that gate. In ADDITION, both handlers run the per-route defence-in-depth
/// <see cref="NodeCallerSessionToken.Validate"/> (trusting the listener gate's
/// <see cref="NodeCallerSessionToken.GatePassedItemKey"/> marker), so the new write route never bypasses
/// caller-auth — the established financial-cluster write posture. Applied to the read (list) route too,
/// uniformly guarding the calendar-collection surface.
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every call resolves the active-team-derived tenant via
/// <see cref="NodeTenant.Resolve"/> server-side (the Harborline App sends no tenant id). The store is
/// composite-<c>(TenantId, Id)</c> keyed, so a foreign tenant's calendars never leak.
/// </para>
/// <para>
/// <b>Closed-over deps</b>, NOT <c>[FromServices]</c> — the routes mount on
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c> whose provider lacks the outer
/// registrations (bug-2849). The hosted endpoint takes the store + accessors in its ctor and passes them
/// into <see cref="Map"/>.
/// </para>
/// </remarks>
public static class CalendarCollectionRoutes
{
    /// <summary>Canonical route for the owned-calendar collection surface.</summary>
    public const string Route = "/api/local-node/calendar/calendars";

    /// <summary>
    /// Maps the list + create calendar-collection routes onto <paramref name="app"/>, closing over the
    /// calendar store, the active-team accessor, and the caller-auth guard.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ICalendarStore calendarStore,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(calendarStore);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(callerAuth);

        // ── GET — list the tenant's owned calendars (default first). ────────────────────────────────
        app.MapGet(Route, async (HttpRequest httpRequest, CancellationToken ct) =>
        {
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
                return NodeCallerSessionToken.RejectResult();

            var tenantId = NodeTenant.Resolve(activeTeam);
            var calendars = await calendarStore.ListAsync(tenantId, ct).ConfigureAwait(false);
            return Results.Ok(new CalendarListResponse(calendars.Select(CalendarWire.From).ToList()));
        });

        // ── POST — create a new owned calendar. ─────────────────────────────────────────────────────
        app.MapPost(Route, async (CreateCalendarBody? body, HttpRequest httpRequest, CancellationToken ct) =>
        {
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
                return NodeCallerSessionToken.RejectResult();

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name_required" });

            if (!TryParseKind(body.Kind, out var kind, out var kindError))
                return Results.BadRequest(new { error = "invalid_kind", detail = kindError });

            ParticipantRef? resourceRef = null;
            if (!string.IsNullOrWhiteSpace(body.Resource))
            {
                if (!TryParseResource(body.Resource, out resourceRef, out var resourceError))
                    return Results.BadRequest(new { error = "invalid_resource", detail = resourceError });
            }

            var tenantId = NodeTenant.Resolve(activeTeam);

            OwnedCalendar calendar;
            try
            {
                // Single-founder MVP: the request principal IS the founder (per-principal principals bind
                // with #118 multi-user enrollment — design §1.4 / Q1). Threading the founder actor into
                // OwnerActorId/CreatedBy keeps the audit consistent with the provisioned default.
                calendar = OwnedCalendar.Create(
                    tenantId,
                    body.Name,
                    kind,
                    ownerActorId: DefaultCalendarProvisioningService.FounderActorId,
                    resourceRef: resourceRef,
                    colorToken: body.ColorToken);
            }
            catch (ArgumentException ex)
            {
                // e.g. a Resource kind without a resource ref, or a non-Resource kind carrying one.
                return Results.BadRequest(new { error = "invalid_calendar", detail = ex.Message });
            }

            await calendarStore.SaveAsync(calendar, ct).ConfigureAwait(false);
            return Results.Created($"{Route}/{calendar.Id.Value}", CalendarWire.From(calendar));
        });
    }

    // ── parsing helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Parse the optional kind ("personal" / "team" / "resource"); defaults to Personal when absent.</summary>
    private static bool TryParseKind(string? raw, out CalendarKind kind, out string error)
    {
        kind = CalendarKind.Personal;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return true; // default = Personal

        switch (raw.Trim().ToLowerInvariant())
        {
            case "personal":
                kind = CalendarKind.Personal;
                return true;
            case "team":
                kind = CalendarKind.Team;
                return true;
            case "resource":
                kind = CalendarKind.Resource;
                return true;
            default:
                error = $"Unknown calendar kind '{raw}' — expected 'personal', 'team', or 'resource'.";
                return false;
        }
    }

    /// <summary>Parse a <c>kind:value</c> resource ref (party:&lt;id&gt; / asset:&lt;id&gt;), mirroring the read routes.</summary>
    private static bool TryParseResource(string raw, out ParticipantRef? resourceRef, out string error)
    {
        resourceRef = null;
        error = string.Empty;

        var sep = raw.IndexOf(':');
        if (sep <= 0 || sep == raw.Length - 1)
        {
            error = $"Resource '{raw}' must be of the form kind:value (party:<id> / asset:<id>).";
            return false;
        }

        var kind = raw[..sep].Trim().ToLowerInvariant();
        var value = raw[(sep + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Resource value must be non-empty.";
            return false;
        }

        switch (kind)
        {
            case "party":
                resourceRef = ParticipantRef.Party(value);
                return true;
            case "asset":
                resourceRef = ParticipantRef.Asset(value);
                return true;
            default:
                error = $"Unknown resource kind '{kind}' — expected 'party' or 'asset'.";
                return false;
        }
    }
}

// ── Wire shapes (camelCase JSON) ──────────────────────────────────────────────────────────────────

/// <summary>The create-calendar request body (camelCase).</summary>
public sealed record CreateCalendarBody(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("colorToken")] string? ColorToken,
    [property: JsonPropertyName("resource")] string? Resource);

/// <summary>One owned calendar on the wire — an opaque id handle + the display name (never a raw ref as label).</summary>
public sealed record CalendarWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("colorToken")] string? ColorToken,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("resource")] string? Resource)
{
    /// <summary>Projects an <see cref="OwnedCalendar"/> onto the wire shape (kind lowercased; resource as kind:value).</summary>
    public static CalendarWire From(OwnedCalendar c) => new(
        Id:         c.Id.Value.ToString(),
        Name:       c.Name,
        Kind:       c.Kind.ToString().ToLowerInvariant(),
        ColorToken: c.ColorToken,
        IsDefault:  c.IsDefault,
        Resource:   c.ResourceRef?.ToString());
}

/// <summary>The list response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record CalendarListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<CalendarWire> Data);
