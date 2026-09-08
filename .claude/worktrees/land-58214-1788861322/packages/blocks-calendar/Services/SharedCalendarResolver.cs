using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The default <see cref="ISharedCalendarResolver"/> (Slice CALENDAR-LAYERS) — composes
/// <see cref="ICalendarSubscriptionStore"/> (which shared calendars a resource subscribes to) +
/// <see cref="ISharedCalendarStore"/> (each calendar's exception spans) into the set of shared
/// holiday/closure days that apply to the resource within a window.
/// </summary>
public sealed class SharedCalendarResolver : ISharedCalendarResolver
{
    private readonly ICalendarSubscriptionStore _subscriptions;
    private readonly ISharedCalendarStore _sharedCalendars;

    public SharedCalendarResolver(ICalendarSubscriptionStore subscriptions, ISharedCalendarStore sharedCalendars)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(sharedCalendars);
        _subscriptions = subscriptions;
        _sharedCalendars = sharedCalendars;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<DateOnly>> ResolveSharedExceptionDaysAsync(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateOnly windowStartDate,
        DateOnly windowEndDate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);

        var days = new HashSet<DateOnly>();

        var subscribedIds = await _subscriptions
            .ListForResourceAsync(tenantId, resourceRef, ct)
            .ConfigureAwait(false);
        if (subscribedIds.Count == 0) return days;

        foreach (var id in subscribedIds)
        {
            var calendar = await _sharedCalendars.GetAsync(tenantId, id, ct).ConfigureAwait(false);
            if (calendar is null) continue;   // a dangling subscription (calendar removed) contributes nothing

            foreach (var span in calendar.Exceptions)
            {
                // Intersect the span with the window, then enumerate only the in-window days (so a
                // multi-year span doesn't materialize every day — only the window slice).
                var from = span.Start > windowStartDate ? span.Start : windowStartDate;
                var to = span.End < windowEndDate ? span.End : windowEndDate;
                for (var d = from; d <= to; d = d.AddDays(1))
                    days.Add(d);
            }
        }

        return days;
    }
}
