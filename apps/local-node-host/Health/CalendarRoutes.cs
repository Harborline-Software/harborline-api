using System.Globalization;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local READ-ONLY calendar surface (ONR app-calendar survey 2026-06-24, the thin first
/// slice). Two GET surfaces:
/// <list type="bullet">
///   <item><b>Agenda</b> — <c>GET /api/local-node/calendar/occurrences?calendarId=&lt;guid&gt;&amp;resource=&lt;kind:value&gt;&amp;fromUtc=&lt;iso&gt;&amp;toUtc=&lt;iso&gt;</c>
///     returns the owning calendar's concrete event occurrences in the UTC window. Resource calendars
///     may also send their resource ref so legacy resource-only events remain visible.</item>
///   <item><b>Free/busy</b> — <c>GET /api/local-node/calendar/free-busy?resource=&lt;kind:value&gt;&amp;fromUtc=&lt;iso&gt;&amp;toUtc=&lt;iso&gt;</c>
///     returns the free (bookable) slots + busy intervals over the UTC window.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>READ-ONLY.</b> Zero write path, zero booking, zero auth-decision surface. The viewer-scoped
/// visibility clip (<c>FreeBusyForViewer</c> / <c>IEventDetailVisibilityPolicy</c>) and the booking
/// write path are out of scope (survey inc-2 / inc-3) — this slice uses the plain, non-viewer reads.
/// </para>
/// <para>
/// <b>UTC instants over the wire.</b> The block's S1 <c>ExpandInstants</c> / <c>FreeBusy</c> already
/// resolve DST to UTC; the wire carries ISO-8601 UTC instants (round-trip <c>"O"</c> format). The
/// client renders in local time — the JS side does NOT re-run DST (no second, divergent DST engine).
/// The query params <c>fromUtc</c> / <c>toUtc</c> are likewise UTC instants.
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> — the same posture as <see cref="InvoiceRoutes"/> /
/// <see cref="InvoiceApprovalTaskRoutes"/>. The Harborline App sends NO tenant id; the block's stores are
/// composite-<c>(TenantId, …)</c> keyed so a foreign-tenant resource never leaks another tenant's
/// events into this view (cross-tenant isolated). A malformed resource ref / window resolves to a
/// uniform 400; an unknown resource resolves to an empty agenda / no free slots (no existence leak).
/// </para>
/// <para>
/// <b>Closed-over deps</b>, NOT <c>[FromServices]</c> — the routes mount on
/// <c>SharedHostedWebApp</c>'s inner <c>WebApplication</c> whose provider lacks the outer
/// registrations (bug-2849). The hosted endpoint takes the calendar services + the active-team
/// accessor in its ctor and passes them into <see cref="Map"/>.
/// </para>
/// </remarks>
public static class CalendarRoutes
{
    /// <summary>Canonical route base for the read-only calendar surface.</summary>
    public const string RouteBase = "/api/local-node/calendar";

    /// <summary>
    /// Maps the agenda + free/busy read routes onto <paramref name="app"/>, closing over the calendar
    /// query / expansion / free-busy services + the event store.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ICalendarParticipantCalendarQuery participantQuery,
        ICalendarEventStore eventStore,
        ICalendarEventExpansionService expansion,
        IFreeBusyService freeBusy,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(participantQuery);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(expansion);
        ArgumentNullException.ThrowIfNull(freeBusy);
        ArgumentNullException.ThrowIfNull(activeTeam);

        MapOccurrences(app, participantQuery, eventStore, expansion, activeTeam);
        MapFreeBusy(app, freeBusy, activeTeam);
    }

    // ── GET /api/local-node/calendar/occurrences — the owning-calendar agenda (UTC instants) ──────
    private static void MapOccurrences(
        IEndpointRouteBuilder app,
        ICalendarParticipantCalendarQuery participantQuery,
        ICalendarEventStore eventStore,
        ICalendarEventExpansionService expansion,
        IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/occurrences", async (
            string? resource,
            string? calendarId,
            string? fromUtc,
            string? toUtc,
            CancellationToken ct) =>
        {
            CalendarId? parsedCalendarId = null;
            if (!string.IsNullOrWhiteSpace(calendarId))
            {
                if (!Guid.TryParse(calendarId, out var id))
                    return Results.BadRequest(new { error = "invalid_calendar", detail = "A valid calendarId is required." });
                parsedCalendarId = new CalendarId(id);
            }

            ParticipantRef? resourceRef = null;
            if (!string.IsNullOrWhiteSpace(resource)
                && !TryParseResource(resource, out resourceRef, out var resourceError))
            {
                return Results.BadRequest(new { error = "invalid_resource", detail = resourceError });
            }
            if (parsedCalendarId is null && resourceRef is null)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_calendar_target",
                    detail = "A calendarId or resource query parameter is required.",
                });
            }
            if (!TryParseWindow(fromUtc, toUtc, out var fromInstant, out var toInstant, out var windowError))
            {
                return Results.BadRequest(new { error = "invalid_window", detail = windowError });
            }

            var tenantId = NodeTenant.Resolve(activeTeam);

            // The events where this resource participates, whose date-window overlaps the UTC window.
            // EventsFor is date-granular; widen the date bounds to the UTC window's calendar dates so a
            // late-evening occurrence whose UTC instant lands in-window is not missed, then ExpandInstants
            // filters precisely to the UTC window.
            var windowStartDate = DateOnly.FromDateTime(fromInstant.UtcDateTime);
            var windowEndDate = DateOnly.FromDateTime(toInstant.UtcDateTime);
            var eventsById = new Dictionary<Guid, CalendarEvent>();
            if (resourceRef is not null)
            {
                var resourceEvents = await participantQuery
                    .EventsFor(tenantId, resourceRef, windowStartDate, windowEndDate, ct)
                    .ConfigureAwait(false);
                foreach (var calendarEvent in resourceEvents)
                {
                    // When a calendar id is present, the resource is a legacy-compatibility seam:
                    // include unowned historical events, but never leak events owned by a different
                    // calendar that happens to use the same resource.
                    if (parsedCalendarId is null || calendarEvent.CalendarId is null
                        || calendarEvent.CalendarId == parsedCalendarId)
                        eventsById[calendarEvent.Id.Value] = calendarEvent;
                }
            }
            if (parsedCalendarId is not null)
            {
                var calendarEvents = await eventStore.ListAsync(tenantId, ct).ConfigureAwait(false);
                foreach (var calendarEvent in calendarEvents.Where(calendarEvent =>
                    calendarEvent.CalendarId == parsedCalendarId
                    && calendarEvent.End >= windowStartDate
                    && calendarEvent.Start <= windowEndDate))
                {
                    eventsById[calendarEvent.Id.Value] = calendarEvent;
                }
            }

            // Expand each event to UTC instants within the window (EXDATE / RECURRENCE-ID + DST applied),
            // flatten + order by start. This is the agenda the Scheduler renders read-only.
            var instants = eventsById.Values
                .SelectMany(ev => expansion.ExpandInstants(ev, fromInstant, toInstant))
                .OrderBy(o => o.StartUtc)
                .ThenBy(o => o.EventId.Value)
                .Select(CalendarOccurrenceWire.From)
                .ToList();

            return Results.Ok(new CalendarOccurrenceListResponse(instants));
        });
    }

    // ── GET /api/local-node/calendar/free-busy — free slots + busy intervals (UTC) ─────────────────
    private static void MapFreeBusy(
        IEndpointRouteBuilder app,
        IFreeBusyService freeBusy,
        IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/free-busy", async (
            string? resource,
            string? fromUtc,
            string? toUtc,
            CancellationToken ct) =>
        {
            if (!TryParseResource(resource, out var resourceRef, out var resourceError))
            {
                return Results.BadRequest(new { error = "invalid_resource", detail = resourceError });
            }
            if (!TryParseWindow(fromUtc, toUtc, out var fromInstant, out var toInstant, out var windowError))
            {
                return Results.BadRequest(new { error = "invalid_window", detail = windowError });
            }

            var tenantId = NodeTenant.Resolve(activeTeam);

            // The plain, non-viewer free/busy (no principal needed for the read MVP). A resource with no
            // availability record yields no free slots (availability is the supply that must exist first);
            // a foreign-tenant resource is cross-tenant isolated by the store keying.
            var result = await freeBusy
                .FreeBusy(tenantId, resourceRef, fromInstant, toInstant, ct)
                .ConfigureAwait(false);

            return Results.Ok(CalendarFreeBusyResponse.From(result));
        });
    }

    // ── parsing helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a <c>kind:value</c> resource ref (e.g. <c>party:p-123</c> / <c>asset:room-7</c>) into a
    /// <see cref="ParticipantRef"/>. The wire form mirrors <see cref="ParticipantRef.ToString"/>.
    /// </summary>
    private static bool TryParseResource(string? raw, out ParticipantRef resourceRef, out string error)
    {
        resourceRef = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A 'resource' query param of the form kind:value (party:<id> / asset:<id>) is required.";
            return false;
        }

        var sep = raw.IndexOf(':', StringComparison.Ordinal);
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

    /// <summary>
    /// Parse the <c>fromUtc</c> / <c>toUtc</c> ISO-8601 UTC instants into a validated window
    /// (<paramref name="from"/> strictly before <paramref name="to"/>, both normalized to UTC).
    /// </summary>
    private static bool TryParseWindow(
        string? fromUtc, string? toUtc,
        out DateTimeOffset from, out DateTimeOffset to, out string error)
    {
        from = default;
        to = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(fromUtc) || string.IsNullOrWhiteSpace(toUtc))
        {
            error = "Both 'fromUtc' and 'toUtc' ISO-8601 UTC instants are required.";
            return false;
        }
        if (!DateTimeOffset.TryParse(
                fromUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out from))
        {
            error = $"'fromUtc' is not a valid ISO-8601 instant: '{fromUtc}'.";
            return false;
        }
        if (!DateTimeOffset.TryParse(
                toUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out to))
        {
            error = $"'toUtc' is not a valid ISO-8601 instant: '{toUtc}'.";
            return false;
        }
        if (to <= from)
        {
            error = "'toUtc' must be strictly after 'fromUtc'.";
            return false;
        }
        return true;
    }
}

// ── Wire shapes (camelCase JSON; UTC instants as ISO-8601 "O" strings) ───────────────────────────

/// <summary>One concrete event occurrence on the agenda — a UTC instant pair (the read-only view).</summary>
public sealed record CalendarOccurrenceWire(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("recurrenceId")] string RecurrenceId,
    [property: JsonPropertyName("startUtc")] string StartUtc,
    [property: JsonPropertyName("endUtc")] string EndUtc,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isOverride")] bool IsOverride)
{
    /// <summary>Projects a block <see cref="OccurrenceInstant"/> onto the wire shape (UTC instants).</summary>
    public static CalendarOccurrenceWire From(OccurrenceInstant o) => new(
        EventId:      o.EventId.Value.ToString(),
        RecurrenceId: o.RecurrenceId.ToString("O", CultureInfo.InvariantCulture),
        StartUtc:     o.StartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        EndUtc:       o.EndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Title:        o.Title,
        IsOverride:   o.IsOverride);
}

/// <summary>The agenda response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record CalendarOccurrenceListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<CalendarOccurrenceWire> Data);

/// <summary>A free or busy UTC interval (the free/busy overlay).</summary>
public sealed record CalendarIntervalWire(
    [property: JsonPropertyName("startUtc")] string StartUtc,
    [property: JsonPropertyName("endUtc")] string EndUtc)
{
    /// <summary>Projects a block <see cref="TimeInterval"/> onto the wire shape (UTC instants).</summary>
    public static CalendarIntervalWire From(TimeInterval i) => new(
        StartUtc: i.StartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        EndUtc:   i.EndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

/// <summary>The free/busy response — the resource + window + free slots + busy intervals (UTC).</summary>
public sealed record CalendarFreeBusyResponse(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("windowStartUtc")] string WindowStartUtc,
    [property: JsonPropertyName("windowEndUtc")] string WindowEndUtc,
    [property: JsonPropertyName("freeSlots")] IReadOnlyList<CalendarIntervalWire> FreeSlots,
    [property: JsonPropertyName("busyIntervals")] IReadOnlyList<CalendarIntervalWire> BusyIntervals)
{
    /// <summary>Projects a block <see cref="FreeBusyResult"/> onto the wire shape.</summary>
    public static CalendarFreeBusyResponse From(FreeBusyResult r) => new(
        Resource:       r.ResourceRef.ToString(),
        WindowStartUtc: r.WindowStartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        WindowEndUtc:   r.WindowEndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        FreeSlots:      r.FreeSlots.Select(CalendarIntervalWire.From).ToList(),
        BusyIntervals:  r.BusyIntervals.Select(CalendarIntervalWire.From).ToList());
}
