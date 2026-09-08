using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Scheduling;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>PBAC-guarded, tenant-derived scheduling definition draft routes.</summary>
public static class SchedulingDefinitionRoutes
{
    public const string RouteBase = "/api/local-node/scheduling/definitions";
    public const string SubjectRoute = "/api/local-node/scheduling/subjects";
    public const string AppointmentRoute = "/api/local-node/scheduling/appointments";
    public const string EventRoute = "/api/local-node/scheduling/events";
    public const string ResourceAvailabilityRoute = "/api/local-node/scheduling/resources/availability";
    private static readonly Guid LocalSchedulingActor = new("5ced0000-0000-0000-0000-000000000001");

    public static void Map(IEndpointRouteBuilder app, NodeSchedulingDraftStore store,
        SchedulingDraftValidator validator, NodeEfPartyRepository parties, IActiveTeamAccessor activeTeam,
        ICurrentUser currentUser, IBookingService bookingService,
        ICalendarEventStore eventStore, ICalendarStore calendarStore,
        IResourceAvailabilityStore availabilityStore,
        IFreeBusyService freeBusyService, TimeProvider timeProvider)
    {
        // Ticket 205 slice 4: ONE tenant resolution point for this route family. Each guard and the work
        // that follows it read the same request's active-team tenant, resolved per call (never captured).
        TenantId Tenant() => NodeTenant.Resolve(activeTeam);

        app.MapGet(RouteBase, async (HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingRead, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            return Results.Ok(await store.ListAsync(Tenant().Value, ct).ConfigureAwait(false));
        });

        app.MapGet($"{RouteBase}/{{definitionId}}", async (string definitionId, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingRead, RouteRecord.Of(definitionId), ct) is { } denied)
                return denied;
            var draft = await store.GetAsync(Tenant().Value, definitionId, ct).ConfigureAwait(false);
            return draft is null
                ? Results.NotFound(new { code = "scheduling.draft.not_found" })
                : Results.Ok(draft);
        });

        // Ticket 088: the retained revision history (SaveAsync is append-only; every prior
        // revision is durably kept) becomes readable, and restore is the Forms append-only
        // lineage semantics adapted to this store: re-save revision N's body as head+1. With no
        // Draft/Published split here, the restored revision IS the new head immediately.
        app.MapGet($"{RouteBase}/{{definitionId}}/versions", async (string definitionId, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingRead, RouteRecord.Of(definitionId), ct) is { } denied)
                return denied;
            var revisions = await store.ListRevisionsAsync(Tenant().Value, definitionId, ct).ConfigureAwait(false);
            return revisions.Count == 0
                ? Results.NotFound(new { code = "scheduling.draft.not_found" })
                : Results.Ok(revisions);
        });

        app.MapGet($"{RouteBase}/{{definitionId}}/versions/{{revision:int}}", async (
            string definitionId, int revision, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingRead, RouteRecord.Of(definitionId), ct) is { } denied)
                return denied;
            var view = await store.GetRevisionAsync(Tenant().Value, definitionId, revision, ct).ConfigureAwait(false);
            return view is null
                ? Results.NotFound(new { code = "scheduling.draft.revision_not_found" })
                : Results.Ok(view);
        });

        app.MapPost($"{RouteBase}/{{definitionId}}/restore", async (
            string definitionId, SchedulingRestoreRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingAuthor, RouteRecord.Of(definitionId), ct) is { } denied)
                return denied;
            var tenant = Tenant().Value;
            var source = await store.GetRevisionAsync(tenant, definitionId, request.Revision, ct).ConfigureAwait(false);
            if (source is null)
                return Results.NotFound(new { code = "scheduling.draft.revision_not_found" });
            var head = await store.GetAsync(tenant, definitionId, ct).ConfigureAwait(false);
            try
            {
                var saved = await store.SaveAsync(tenant, definitionId, source.Definition,
                    head!.Revision, currentUser.UserId, ct).ConfigureAwait(false);
                // Lineage is response-only: the audit row has no free field, and adding a column
                // is the separately-decided store migration (ticket 088's recorded ruling).
                return Results.Ok(new { definitionId, revision = saved.Revision, restoredFrom = request.Revision });
            }
            catch (SchedulingDraftConflictException ex)
            {
                return Results.Conflict(new { code = "scheduling.draft.revision_conflict", currentRevision = ex.CurrentRevision });
            }
        });

        app.MapPut($"{RouteBase}/{{definitionId}}/draft", async (
            string definitionId, SchedulingDraftSaveRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingAuthor, RouteRecord.Of(definitionId), ct) is { } denied)
                return denied;
            try
            {
                var saved = await store.SaveAsync(Tenant().Value, definitionId,
                    request.Definition, request.ExpectedRevision, currentUser.UserId, ct).ConfigureAwait(false);
                return Results.Ok(saved);
            }
            catch (SchedulingDraftConflictException ex)
            {
                return Results.Conflict(new { code = "scheduling.draft.revision_conflict", currentRevision = ex.CurrentRevision });
            }
            catch (SchedulingDraftShapeException ex)
            {
                return Results.BadRequest(new { code = ex.Code });
            }
        });

        app.MapPost($"{RouteBase}/validate", async (
            SchedulingDraftValidateRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingAuthor, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            var issues = validator.Validate(request.Definition);
            return Results.Ok(new { valid = issues.Count == 0, issues });
        });

        // Purpose-limited subject search for the dogfood front-office flow. It returns only the
        // minimum scheduling picker fields, is active-team scoped, and cannot fall through to the
        // broader contacts management surface. A future visibility adapter may narrow this owner-only
        // dogfood projection further without changing the wire.
        app.MapGet(SubjectRoute, async (HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingOperate, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            var tenant = Tenant();
            var subjects = await parties.ListByTenantAsync(tenant, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                subjects = subjects.Select(subject => new
                {
                    id = subject.Id.Value,
                    displayName = subject.DisplayName,
                }).ToArray(),
            });
        });

        app.MapPost(AppointmentRoute, async (SchedulingAppointmentRequest request, HttpContext http, CancellationToken ct) =>
        {
            // The body MAY name the definition this appointment is booked against, and when it does the act
            // addresses that record - so the guard names it. Only a body with no definition addresses the
            // install (the generic calendar substrate), and scheduling:operate is declared install-wide for
            // exactly that shape.
            var definitionId = request.DefinitionId;
            var addressed = string.IsNullOrWhiteSpace(definitionId)
                ? RouteRecord.TheInstall
                : RouteRecord.Of(definitionId);
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingOperate, addressed, ct) is { } denied)
                return denied;
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { code = "scheduling.appointment.invalid" });
            var resource = ParseParticipant(request.Resource);
            if (resource is null)
                return Results.BadRequest(new { code = "scheduling.appointment.resource_invalid" });

            var tenant = Tenant();
            var endUtc = request.EndUtc;
            EventPadding? padding = null;
            if (request.DefinitionId is not null)
            {
                if (string.IsNullOrWhiteSpace(request.DefinitionId))
                    return Results.BadRequest(new { code = "scheduling.appointment.definition_invalid" });
                var draft = await store.GetAsync(tenant.Value, request.DefinitionId, ct).ConfigureAwait(false);
                if (draft is null)
                    return Results.NotFound(new { code = "scheduling.appointment.definition_not_found" });
                if (!SchedulingAppointmentPolicy.TryFromDefinition(draft.Definition, out var policy))
                    return Results.BadRequest(new { code = "scheduling.appointment.definition_invalid" });

                var leadFloor = timeProvider.GetUtcNow().AddMinutes(policy!.MinimumLeadTimeMinutes);
                if (request.StartUtc < leadFloor)
                    return Results.Conflict(new { code = "scheduling.appointment.minimum_lead_time" });
                endUtc = policy.EndFor(request.StartUtc);
                var appointmentPadding = policy.Padding;
                padding = appointmentPadding;

                // Appointment-type fit is stricter than the generic calendar substrate: its entire
                // setup/body/cleanup footprint must fit a canonical free slot. The generic booking
                // service intentionally permits padding to spill beyond availability, so enforce the
                // appointment-type policy here before delegating the actual write.
                var footprint = new TimeInterval(
                    request.StartUtc - appointmentPadding.Pre,
                    endUtc + appointmentPadding.Post);
                var freeBusy = await freeBusyService
                    .FreeBusy(tenant, resource, footprint.StartUtc, footprint.EndUtc, ct)
                    .ConfigureAwait(false);
                if (!freeBusy.IsFree(footprint))
                {
                    var overlapsBusy = freeBusy.BusyIntervals.Any(interval => interval.Overlaps(footprint));
                    var code = overlapsBusy
                        ? "scheduling.appointment.slot_conflict"
                        : "scheduling.appointment.no_availability";
                    return Results.Conflict(new { code });
                }
            }
            else if (endUtc <= request.StartUtc)
            {
                return Results.BadRequest(new { code = "scheduling.appointment.invalid" });
            }

            var outcome = await bookingService.Book(
                tenant, resource, request.Title.Trim(), request.StartUtc,
                endUtc, LocalSchedulingActor, ParticipantRef.Party(request.SubjectId),
                padding: padding, ct: ct)
                .ConfigureAwait(false);
            return outcome.Success
                ? Results.Ok(new { eventId = outcome.Event!.Id.Value, status = "booked" })
                : Results.Conflict(new { code = $"scheduling.appointment.{outcome.RejectionReason!.ToLowerInvariant()}" });
        });

        app.MapPost(EventRoute, async (SchedulingEventRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingOperate, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { code = "scheduling.event.invalid" });
            var tenant = Tenant();
            OwnedCalendar? targetCalendar = null;
            ParticipantRef? resource = null;
            if (!string.IsNullOrWhiteSpace(request.CalendarId))
            {
                if (!Guid.TryParse(request.CalendarId, out var calendarId))
                    return Results.BadRequest(new { code = "scheduling.event.calendar_invalid" });

                targetCalendar = await calendarStore
                    .GetAsync(tenant, new CalendarId(calendarId), ct)
                    .ConfigureAwait(false);
                if (targetCalendar is null)
                    return Results.NotFound(new { code = "scheduling.event.calendar_not_found" });

                // The stored calendar is authoritative for its resource lens. Never trust a client
                // to pair an arbitrary resource with a calendar id.
                resource = targetCalendar.ResourceRef;
            }
            else
            {
                // Compatibility for existing dogfood callers while the calendar-id contract rolls
                // through. New clients always send CalendarId so Personal/Team calendars are valid.
                resource = ParseParticipant(request.Resource ?? string.Empty);
                if (resource is null)
                    return Results.BadRequest(new { code = "scheduling.event.resource_invalid" });
            }

            DateOnly startDate;
            DateOnly endDate;
            string timezoneId;
            TimeOnly? startTime;
            TimeOnly? endTime;

            if (request.AllDay)
            {
                if (request.StartUtc is not null || request.EndUtc is not null)
                    return Results.BadRequest(new { code = "scheduling.event.all_day_time_fields_forbidden" });
                if (!TryParseCivilDateRange(request.StartDate, request.EndDate, out startDate, out endDate))
                    return Results.BadRequest(new { code = "scheduling.event.all_day_date_invalid" });

                // All-day events are stored as civil dates. UTC is only the inert aggregate default;
                // no timezone conversion participates in deriving or persisting the dates.
                timezoneId = "UTC";
                startTime = null;
                endTime = null;
            }
            else
            {
                if (request.StartDate is not null || request.EndDate is not null)
                    return Results.BadRequest(new { code = "scheduling.event.timed_date_fields_forbidden" });
                if (request.StartUtc is not { } startUtc || request.EndUtc is not { } endUtc
                    || endUtc <= startUtc || string.IsNullOrWhiteSpace(request.Timezone))
                    return Results.BadRequest(new { code = "scheduling.event.invalid" });

                TimeZoneInfo timezone;
                try
                {
                    timezone = TimezoneResolver.Resolve(request.Timezone);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    return Results.BadRequest(new { code = "scheduling.event.timezone_invalid" });
                }

                var start = TimeZoneInfo.ConvertTime(startUtc, timezone);
                var end = TimeZoneInfo.ConvertTime(endUtc, timezone);
                startDate = DateOnly.FromDateTime(start.DateTime);
                endDate = DateOnly.FromDateTime(end.DateTime);
                timezoneId = request.Timezone;
                startTime = TimeOnly.FromDateTime(start.DateTime);
                endTime = TimeOnly.FromDateTime(end.DateTime);
            }

            var calendarEvent = CalendarEvent.Create(
                tenant, request.Title.Trim(),
                startDate, endDate, LocalSchedulingActor, timezone: timezoneId,
                startTime: startTime, endTime: endTime, occupancy: Occupancy.Blocking,
                allDay: request.AllDay);
            if (targetCalendar is not null)
                calendarEvent.SetCalendarId(targetCalendar.Id, LocalSchedulingActor);
            if (resource is not null)
                calendarEvent.SetResource(resource, LocalSchedulingActor);
            await eventStore.SaveAsync(calendarEvent, ct).ConfigureAwait(false);
            return Results.Ok(new { eventId = calendarEvent.Id.Value, status = "created" });
        });

        app.MapPost(ResourceAvailabilityRoute, async (
            SchedulingResourceAvailabilityRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (await RequestAuthorization.RefusalAsync(
                    http, Tenant(), Permission.SchedulingOperate, RouteRecord.TheInstall, ct) is { } denied)
                return denied;
            var resource = ParseParticipant(request.Resource);
            if (resource is null || resource.Kind != ParticipantKind.Party)
                return Results.BadRequest(new { code = "scheduling.resource.invalid" });
            var tenant = Tenant();
            var party = await parties.GetByIdAsync(new PartyId(resource.Value), ct).ConfigureAwait(false);
            if (party is null || party.TenantId != tenant)
                return Results.NotFound(new { code = "scheduling.resource.not_found" });

            var timezone = TimezoneResolver.Resolve(request.Timezone);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timezone).DateTime);
            var availability = ResourceAvailability.Create(tenant, resource, request.Timezone)
                .AddWindow(AvailabilityWindow.Create(
                    today, new TimeOnly(8, 0), new TimeOnly(18, 0),
                    "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"));
            await availabilityStore.SaveAsync(availability, ct).ConfigureAwait(false);
            return Results.Ok(new { status = "ready", resource = request.Resource });
        });
    }

    private static ParticipantRef? ParseParticipant(string value)
    {
        var parts = value.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1])) return null;
        return parts[0] switch
        {
            "party" => ParticipantRef.Party(parts[1]),
            "asset" => ParticipantRef.Asset(parts[1]),
            _ => null,
        };
    }

    private static bool TryParseCivilDateRange(
        string? rawStart,
        string? rawEnd,
        out DateOnly start,
        out DateOnly end)
    {
        const string CivilDateFormat = "yyyy-MM-dd";
        start = default;
        end = default;
        return DateOnly.TryParseExact(
                rawStart, CivilDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out start)
            && DateOnly.TryParseExact(
                rawEnd, CivilDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out end)
            && end >= start;
    }

}

public sealed record SchedulingDraftSaveRequest(int ExpectedRevision, JsonElement Definition);
public sealed record SchedulingDraftValidateRequest(JsonElement Definition);
public sealed record SchedulingRestoreRequest(int Revision);
public sealed record SchedulingAppointmentRequest(
    string SubjectId, string Resource, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    string? DefinitionId = null);
public sealed record SchedulingEventRequest(
    string Title,
    string? CalendarId = null,
    string? Resource = null,
    string? Timezone = null,
    DateTimeOffset? StartUtc = null,
    DateTimeOffset? EndUtc = null,
    bool AllDay = false,
    string? StartDate = null,
    string? EndDate = null);
public sealed record SchedulingResourceAvailabilityRequest(string Resource, string Timezone);
