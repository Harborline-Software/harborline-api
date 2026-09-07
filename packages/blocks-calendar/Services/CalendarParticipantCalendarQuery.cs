using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The default <see cref="ICalendarParticipantCalendarQuery"/> — composes the
/// <see cref="ICalendarEventStore"/> (the tenant-scoped event source of truth) with the
/// <see cref="ICalendarEventExpansionService"/> (EXDATE / RECURRENCE-ID occurrence semantics). It
/// reads one participant's calendar by filtering the tenant's events to those the participant is in,
/// then keeping only those with an occurrence in the window.
/// </summary>
/// <remarks>
/// "A calendar is a view": there is no per-participant calendar <i>store</i> — the calendar is
/// derived from the participation set on the shared events. That keeps the event the single source
/// of truth (the same booking appears on the doctor's, the patient's, and the room's calendar with
/// no duplication or cross-calendar sync).
/// </remarks>
public sealed class CalendarParticipantCalendarQuery : ICalendarParticipantCalendarQuery
{
    private readonly ICalendarEventStore _store;
    private readonly ICalendarEventExpansionService _expansion;

    public CalendarParticipantCalendarQuery(
        ICalendarEventStore store,
        ICalendarEventExpansionService expansion)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(expansion);
        _store = store;
        _expansion = expansion;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarEvent>> EventsFor(
        TenantId tenantId,
        ParticipantRef participant,
        DateOnly windowStart,
        DateOnly windowEnd,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (windowEnd < windowStart)
            throw new ArgumentException("windowEnd must be on or after windowStart.", nameof(windowEnd));

        // ListAsync is tenant-scoped: only this tenant's events are ever considered (cross-tenant
        // isolation is enforced at the store, the participant ref alone can never reach another
        // tenant's events).
        var tenantEvents = await _store.ListAsync(tenantId, ct).ConfigureAwait(false);

        var result = new List<CalendarEvent>();
        foreach (var ev in tenantEvents)
        {
            // Participant must be in this event (in any role) ...
            if (!ev.Participations.Any(p => p.Participant == participant))
                continue;

            // ... and the event must actually land in the window (a recurring series may have no
            // occurrence here; a cancelled event/series expands to nothing).
            if (_expansion.Expand(ev, windowStart, windowEnd).Count > 0)
                result.Add(ev);
        }

        result.Sort(static (a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : a.Id.Value.CompareTo(b.Id.Value);
        });
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventOccurrence>> OccurrencesFor(
        TenantId tenantId,
        ParticipantRef participant,
        DateOnly windowStart,
        DateOnly windowEnd,
        CancellationToken ct = default)
    {
        var events = await EventsFor(tenantId, participant, windowStart, windowEnd, ct).ConfigureAwait(false);

        var occurrences = events
            .SelectMany(ev => _expansion.Expand(ev, windowStart, windowEnd))
            .ToList();

        occurrences.Sort(static (a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            if (byStart != 0) return byStart;
            var byEvent = a.EventId.Value.CompareTo(b.EventId.Value);
            return byEvent != 0 ? byEvent : a.RecurrenceId.CompareTo(b.RecurrenceId);
        });
        return occurrences;
    }
}
