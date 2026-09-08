using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The default <see cref="ICalendarContextQuery"/> (Slice S3) — composes the
/// <see cref="ICalendarEventStore"/> (tenant-scoped event source of truth) with the
/// <see cref="ICalendarEventExpansionService"/> (occurrence semantics) and filters the tenant's
/// events to those scheduled against the requested context with an occurrence in the window.
/// Structurally identical to <see cref="CalendarParticipantCalendarQuery"/> but keyed on the opaque
/// <see cref="CalendarEvent.ScheduledAgainst"/> ref instead of the participation set — the "by
/// context" axis next to the "by participant" axis.
/// </summary>
public sealed class CalendarContextQuery : ICalendarContextQuery
{
    private readonly ICalendarEventStore _store;
    private readonly ICalendarEventExpansionService _expansion;

    public CalendarContextQuery(ICalendarEventStore store, ICalendarEventExpansionService expansion)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(expansion);
        _store = store;
        _expansion = expansion;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarEvent>> EventsForContext(
        TenantId tenantId,
        ContextRef context,
        DateOnly windowStart,
        DateOnly windowEnd,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (windowEnd < windowStart)
            throw new ArgumentException("windowEnd must be on or after windowStart.", nameof(windowEnd));

        // Tenant-scoped: ListAsync only ever returns this tenant's events, so cross-tenant isolation
        // holds even if a context ref value collides across tenants.
        var tenantEvents = await _store.ListAsync(tenantId, ct).ConfigureAwait(false);

        var result = new List<CalendarEvent>();
        foreach (var ev in tenantEvents)
        {
            // Exact opaque-ref match on the context (ContextRef has value equality over (Kind, Value)).
            if (ev.ScheduledAgainst != context)
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
}
