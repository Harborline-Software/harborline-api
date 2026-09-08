using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The default <see cref="IBookingService"/> (Slice S3, Direction A). Composes
/// <see cref="IFreeBusyService"/> (the no-double-book guard) + the availability supply
/// (<see cref="IResourceAvailabilityStore"/> + <see cref="IAvailabilityExpansionService"/>, for the
/// "is the slot inside an availability window" pre-gate and the resource's timezone) +
/// <see cref="ICalendarEventStore"/> (where the booking lands). It reuses the <i>same</i> availability
/// expansion free/busy uses — one source of truth for the supply, no re-implemented RRULE.
/// </summary>
/// <remarks>
/// <b>The booking gate matches the free/busy view (Slice CALENDAR-LAYERS).</b> The
/// <see cref="ISharedCalendarResolver"/> is wired here so the availability pre-gate composes the SAME
/// supply-side exception layers <see cref="FreeBusyService"/> does — the resource's own
/// single-day/spanning exceptions (applied inside <c>Expand</c>) AND the subscribed shared-calendar
/// holidays/closures (the resolved <c>additionalExceptionDays</c>). Without this, a resource subscribed
/// to a "clinic closed Wednesday" shared calendar would show Wednesday busy in free/busy yet still admit
/// a Wednesday booking — a view↔gate inconsistency. A <see langword="null"/> resolver disables the
/// shared-calendar layer (the resource's own exceptions only — the S3 behavior).
/// </remarks>
public sealed class BookingService : IBookingService
{
    private readonly IFreeBusyService _freeBusy;
    private readonly IResourceAvailabilityStore _availabilityStore;
    private readonly IAvailabilityExpansionService _availabilityExpansion;
    private readonly ICalendarEventStore _eventStore;
    private readonly IPaddingPolicy _paddingPolicy;
    private readonly ISharedCalendarResolver? _sharedCalendarResolver;

    /// <summary>
    /// The S3 constructor (no calendar-layers wiring) — the base availability pre-gate applies only the
    /// resource's OWN exceptions; shared-calendar holidays are not composed into the booking gate.
    /// </summary>
    public BookingService(
        IFreeBusyService freeBusy,
        IResourceAvailabilityStore availabilityStore,
        IAvailabilityExpansionService availabilityExpansion,
        ICalendarEventStore eventStore,
        IPaddingPolicy paddingPolicy)
        : this(freeBusy, availabilityStore, availabilityExpansion, eventStore, paddingPolicy,
               sharedCalendarResolver: null)
    {
    }

    /// <summary>
    /// The CALENDAR-LAYERS constructor — additionally composes the subscribed shared-calendar
    /// holidays/closures (<paramref name="sharedCalendarResolver"/>) into the availability pre-gate so
    /// the booking gate matches the free/busy view. A <see langword="null"/> resolver disables the
    /// shared-calendar layer (the resource's own exceptions only).
    /// </summary>
    public BookingService(
        IFreeBusyService freeBusy,
        IResourceAvailabilityStore availabilityStore,
        IAvailabilityExpansionService availabilityExpansion,
        ICalendarEventStore eventStore,
        IPaddingPolicy paddingPolicy,
        ISharedCalendarResolver? sharedCalendarResolver)
    {
        ArgumentNullException.ThrowIfNull(freeBusy);
        ArgumentNullException.ThrowIfNull(availabilityStore);
        ArgumentNullException.ThrowIfNull(availabilityExpansion);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(paddingPolicy);
        _freeBusy = freeBusy;
        _availabilityStore = availabilityStore;
        _availabilityExpansion = availabilityExpansion;
        _eventStore = eventStore;
        _paddingPolicy = paddingPolicy;
        _sharedCalendarResolver = sharedCalendarResolver;
    }

    /// <inheritdoc />
    public async Task<BookingOutcome> Book(
        TenantId tenantId,
        ParticipantRef resourceRef,
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid bookedBy,
        ParticipantRef? attendee = null,
        ContextRef? scheduledAgainst = null,
        EventPadding? padding = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title must be non-empty.", nameof(title));
        if (endUtc <= startUtc)
            return BookingOutcome.Rejected(BookingOutcome.SlotInverted);

        // The VISIBLE (booked / shown) slot — what the patient sees and what is stored.
        var visible = new TimeInterval(startUtc, endUtc);

        // Resolve the padding: a per-event override (passed in) wins over the configured policy default
        // (defaulted-at-booking). EventPadding.None ⇒ the candidate occupies exactly its visible slot.
        var effectivePadding = (padding is { } p)
            ? EventPadding.Of(p.Pre, p.Post)
            : _paddingPolicy.ResolveDefault(tenantId, resourceRef);

        // The OCCUPIED footprint — [start − pre, end + post). The no-double-book guard is judged against
        // THIS, not the visible slot: a padded booking must not collide with existing occupancy over its
        // full footprint. Padding is added to the UTC instants (never wall-clock) — the UTC invariant.
        var occupied = effectivePadding.IsNone
            ? visible
            : new TimeInterval(startUtc - effectivePadding.Pre, endUtc + effectivePadding.Post);

        // The availability record is what makes the slot bookable AND carries the resource's tz.
        var availability = await _availabilityStore.GetAsync(tenantId, resourceRef, ct).ConfigureAwait(false);
        if (availability is null)
            return BookingOutcome.Rejected(BookingOutcome.NoAvailability);

        // The VISIBLE slot must lie inside the resource's availability supply (else NO_AVAILABILITY).
        // Availability bounds what the demand side can BOOK (you can't take an appointment past close);
        // padding (the doctor's post-visit documentation) may legitimately spill past the window edge,
        // so the availability test uses the visible slot, not the occupied footprint. Reuse the SAME
        // layered expansion free/busy uses — one source of truth, no re-implemented RRULE — so the
        // booking gate composes the resource's OWN exceptions (inside Expand) AND the subscribed
        // SHARED-calendar holidays/closures (the resolver). Without the shared layer the gate would admit
        // a booking on a day free/busy shows as a shared holiday (the view↔gate inconsistency S1 names).
        var sharedExceptionDays = await ResolveSharedExceptionDays(tenantId, resourceRef, startUtc, endUtc, ct)
            .ConfigureAwait(false);
        var supply = sharedExceptionDays is { Count: > 0 }
            ? _availabilityExpansion.Expand(availability, startUtc, endUtc, sharedExceptionDays)
            : _availabilityExpansion.Expand(availability, startUtc, endUtc);
        var withinAvailability = supply.Any(s => visible.StartUtc >= s.StartUtc && visible.EndUtc <= s.EndUtc);
        if (!withinAvailability)
            return BookingOutcome.Rejected(BookingOutcome.NoAvailability);

        // Available but possibly already occupied — the no-double-book guard. The candidate's OCCUPIED
        // footprint must not overlap any existing occupancy (each existing event's own occupied footprint,
        // padding included). We test against the raw occupied intervals (NOT free slots): the candidate's
        // padding may legitimately spill past an availability edge, so requiring the footprint to sit
        // inside a free slot would wrongly reject a padded-past-close booking. Overlap (half-open) is the
        // double-book condition; touching endpoints are back-to-back, not a conflict.
        var existingOccupancy = await _freeBusy
            .OccupiedIntervals(tenantId, resourceRef, occupied.StartUtc, occupied.EndUtc, ct)
            .ConfigureAwait(false);
        if (existingOccupancy.Any(b => b.Overlaps(occupied)))
            return BookingOutcome.Rejected(BookingOutcome.SlotConflict);

        // Admitted. Build a single-occurrence Bookable event from the VISIBLE UTC slot, expressed in the
        // resource's local wall-clock + tz (so the stored event reads naturally and re-expands to the
        // same UTC instant), carrying the resolved padding envelope.
        var ev = BuildBookableEvent(
            tenantId, resourceRef, title, startUtc, endUtc, bookedBy, attendee, scheduledAgainst,
            availability.Timezone, effectivePadding);

        await _eventStore.SaveAsync(ev, ct).ConfigureAwait(false);
        return BookingOutcome.Booked(ev);
    }

    /// <summary>
    /// Resolve the subscribed shared-calendar holiday/closure days overlapping the booking window — the
    /// SAME shared-exception layer <see cref="FreeBusyService"/>'s <c>ComposeAvailability</c> applies, so
    /// the booking gate matches the free/busy view. Widens the local-date span by ±1 day to match the
    /// availability expansion's tz-offset cushion. Returns <see langword="null"/> when no resolver is
    /// wired (the S3 path — the resource's own exceptions only).
    /// </summary>
    private async Task<IReadOnlySet<DateOnly>?> ResolveSharedExceptionDays(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct)
    {
        if (_sharedCalendarResolver is null) return null;

        var localStart = DateOnly.FromDateTime(windowStartUtc.UtcDateTime).AddDays(-1);
        var localEnd = DateOnly.FromDateTime(windowEndUtc.UtcDateTime).AddDays(1);
        return await _sharedCalendarResolver
            .ResolveSharedExceptionDaysAsync(tenantId, resourceRef, localStart, localEnd, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Build the single-occurrence <see cref="Occupancy.Bookable"/> event from the UTC slot, expressed
    /// in the resource's local wall-clock + timezone. The event re-expands (via
    /// <c>ExpandInstants</c>) to the same UTC instant, so the booking is internally consistent with
    /// free/busy.
    /// </summary>
    private static CalendarEvent BuildBookableEvent(
        TenantId tenantId,
        ParticipantRef resourceRef,
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid bookedBy,
        ParticipantRef? attendee,
        ContextRef? scheduledAgainst,
        string timezone,
        EventPadding padding)
    {
        var tz = TimezoneResolver.Resolve(timezone);
        var localStart = TimeZoneInfo.ConvertTime(startUtc, tz);
        var localEnd = TimeZoneInfo.ConvertTime(endUtc, tz);

        var startDate = DateOnly.FromDateTime(localStart.DateTime);
        var endDate = DateOnly.FromDateTime(localEnd.DateTime);
        var startTime = TimeOnly.FromDateTime(localStart.DateTime);
        var endTime = TimeOnly.FromDateTime(localEnd.DateTime);

        var ev = CalendarEvent.Create(
            tenantId:  tenantId,
            title:     title,
            start:     startDate,
            end:       endDate,
            createdBy: bookedBy,
            timezone:  timezone,
            startTime: startTime,
            endTime:   endTime,
            occupancy: Occupancy.Bookable,
            padding:   padding);

        ev.SetResource(resourceRef, bookedBy, ParticipationStatus.Confirmed);
        if (attendee is not null)
            ev.AddParticipation(CalendarParticipation.Attendee(attendee), bookedBy);
        if (scheduledAgainst is not null)
            ev.SetScheduledAgainst(scheduledAgainst, bookedBy);

        return ev;
    }
}
