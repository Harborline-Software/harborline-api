using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The default <see cref="IFreeBusyService"/> — composes the availability supply
/// (<see cref="IResourceAvailabilityStore"/> + <see cref="IAvailabilityExpansionService"/>) with the
/// occupancy (<see cref="ICalendarEventStore"/> + <see cref="ICalendarEventExpansionService"/>) and
/// differences them. Slice CALENDAR-LAYERS extends the supply side into a layered composition:
/// <c>effective_free = (base_availability − exceptions[shared holidays + per-resource
/// vacations/closures]) − occupancy[appts + blocks + personal-appts]</c>, and adds a viewer-scoped
/// projection (busy-to-others, detail-to-owner) for private appointments.
/// </summary>
/// <remarks>
/// <para>
/// <b>The UTC invariant (preserved from S3 — load-bearing).</b> Every layer is differenced as
/// absolute UTC instants. The exception LAYERS (shared holidays + per-resource vacations) are applied
/// as whole-day suppressions <i>during availability expansion</i> — a local-date filter <i>before</i>
/// each window's wall-clock→UTC resolution — so the surviving availability intervals, the occupancy
/// intervals, and the <see cref="IntervalMath"/> difference between them are all still in UTC. The DST
/// trap stays a non-issue by construction (a 9 a.m. window keeps its 9 a.m. wall-clock across a DST
/// boundary; its UTC instant shifts; the difference is in UTC either way).
/// </para>
/// <para>
/// <b>Visibility never changes availability.</b> A private appointment still occupies the resource and
/// still blocks a competing booking, so the FREE set is identical for every viewer — only the busy
/// events' detail is viewer-scoped (<see cref="FreeBusyForViewer"/>). This keeps booking-correctness
/// intact: a viewer who cannot see a private appointment's detail still cannot book over it.
/// </para>
/// </remarks>
public sealed class FreeBusyService : IFreeBusyService
{
    private readonly IResourceAvailabilityStore _availabilityStore;
    private readonly IAvailabilityExpansionService _availabilityExpansion;
    private readonly ICalendarEventStore _eventStore;
    private readonly ICalendarEventExpansionService _eventExpansion;
    private readonly ISharedCalendarResolver? _sharedCalendarResolver;
    private readonly IEventDetailVisibilityPolicy _visibilityPolicy;

    /// <summary>
    /// The S3 constructor (no calendar-layers wiring) — the base availability + occupancy composition.
    /// Shared-calendar holidays are not composed (no resolver); the viewer projection uses the safe
    /// owner-only visibility policy.
    /// </summary>
    public FreeBusyService(
        IResourceAvailabilityStore availabilityStore,
        IAvailabilityExpansionService availabilityExpansion,
        ICalendarEventStore eventStore,
        ICalendarEventExpansionService eventExpansion)
        : this(availabilityStore, availabilityExpansion, eventStore, eventExpansion,
               sharedCalendarResolver: null, visibilityPolicy: null)
    {
    }

    /// <summary>
    /// The CALENDAR-LAYERS constructor — additionally composes shared-calendar holidays
    /// (<paramref name="sharedCalendarResolver"/>) and applies <paramref name="visibilityPolicy"/> for
    /// the viewer-scoped detail projection. A <see langword="null"/> resolver disables the shared-
    /// calendar layer (base + per-resource exceptions only); a <see langword="null"/> policy uses the
    /// fail-closed <see cref="OwnerOnlyEventDetailVisibilityPolicy"/>.
    /// </summary>
    public FreeBusyService(
        IResourceAvailabilityStore availabilityStore,
        IAvailabilityExpansionService availabilityExpansion,
        ICalendarEventStore eventStore,
        ICalendarEventExpansionService eventExpansion,
        ISharedCalendarResolver? sharedCalendarResolver,
        IEventDetailVisibilityPolicy? visibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(availabilityStore);
        ArgumentNullException.ThrowIfNull(availabilityExpansion);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(eventExpansion);
        _availabilityStore = availabilityStore;
        _availabilityExpansion = availabilityExpansion;
        _eventStore = eventStore;
        _eventExpansion = eventExpansion;
        _sharedCalendarResolver = sharedCalendarResolver;
        _visibilityPolicy = visibilityPolicy ?? new OwnerOnlyEventDetailVisibilityPolicy();
    }

    /// <inheritdoc />
    public async Task<FreeBusyResult> FreeBusy(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        if (windowEndUtc < windowStartUtc)
            throw new ArgumentException("windowEndUtc must be on or after windowStartUtc.", nameof(windowEndUtc));

        var availableIntervals = await ComposeAvailability(tenantId, resourceRef, windowStartUtc, windowEndUtc, ct)
            .ConfigureAwait(false);

        // --- The occupancy layer: every non-cancelled event on this resource, all occupancy classes ---
        var busyEvents = await GatherOccupancy(tenantId, resourceRef, windowStartUtc, windowEndUtc, ct)
            .ConfigureAwait(false);
        var busyIntervals = busyEvents.Select(b => b.Interval).ToList();

        // free = availability − busy (both merged inside Subtract), then both clipped to the window.
        var free = IntervalMath.Subtract(availableIntervals, busyIntervals);
        free = IntervalMath.ClipAll(free, windowStartUtc, windowEndUtc);

        // Report only the busy that falls inside availability ∩ window (the meaningful occupancy —
        // a block outside any availability window is not "busy time the resource could have offered").
        var busyInsideAvailability = IntervalMath.Subtract(availableIntervals, free);
        busyInsideAvailability = IntervalMath.ClipAll(busyInsideAvailability, windowStartUtc, windowEndUtc);

        free.Sort(static (a, b) => a.StartUtc.CompareTo(b.StartUtc));
        busyInsideAvailability.Sort(static (a, b) => a.StartUtc.CompareTo(b.StartUtc));

        return new FreeBusyResult(
            ResourceRef:    resourceRef,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc:   windowEndUtc,
            FreeSlots:      free,
            BusyIntervals:  busyInsideAvailability);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeInterval>> OccupiedIntervals(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        if (windowEndUtc < windowStartUtc)
            throw new ArgumentException("windowEndUtc must be on or after windowStartUtc.", nameof(windowEndUtc));

        // The raw occupied footprints (each event's visible span extended by its padding), merged and
        // clipped to the window — NOT intersected with availability (occupancy past an availability edge
        // still blocks a booking). This is the no-double-book surface the booking path tests against.
        var busy = await GatherOccupancy(tenantId, resourceRef, windowStartUtc, windowEndUtc, ct)
            .ConfigureAwait(false);
        var merged = IntervalMath.ClipAll(
            IntervalMath.Merge(busy.Select(b => b.Interval).ToList()), windowStartUtc, windowEndUtc);
        merged.Sort(static (a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return merged;
    }

    /// <inheritdoc />
    public async Task<ViewerFreeBusyResult> FreeBusyForViewer(
        TenantId tenantId,
        ParticipantRef resourceRef,
        Guid? viewerActorId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        if (windowEndUtc < windowStartUtc)
            throw new ArgumentException("windowEndUtc must be on or after windowStartUtc.", nameof(windowEndUtc));

        var availableIntervals = await ComposeAvailability(tenantId, resourceRef, windowStartUtc, windowEndUtc, ct)
            .ConfigureAwait(false);

        var busyEvents = await GatherOccupancy(tenantId, resourceRef, windowStartUtc, windowEndUtc, ct)
            .ConfigureAwait(false);

        // FREE is visibility-independent: a private appointment still blocks a booking, so the free set
        // is exactly availability − ALL occupancy, the same for every viewer.
        var free = IntervalMath.Subtract(availableIntervals, busyEvents.Select(b => b.Interval).ToList());
        free = IntervalMath.ClipAll(free, windowStartUtc, windowEndUtc);
        free.Sort(static (a, b) => a.StartUtc.CompareTo(b.StartUtc));

        // BUSY events carry viewer-scoped detail: a Private event the viewer is not authorized for is
        // redacted (the slot still shows, the title is hidden). The interval is clipped to the window;
        // events entirely outside it are dropped.
        var projected = new List<BusyEvent>();
        foreach (var be in busyEvents)
        {
            if (IntervalMath.Clip(be.Interval, windowStartUtc, windowEndUtc) is not { } clipped) continue;
            var canSeeDetail = _visibilityPolicy.CanSeeDetail(be.Event, viewerActorId);
            projected.Add(canSeeDetail
                ? BusyEvent.Visible(clipped, be.Event.Id, be.Event.Title, be.Event.Visibility)
                : BusyEvent.Redacted(clipped, be.Event.Id, be.Event.Visibility));
        }
        projected.Sort(static (a, b) => a.Interval.StartUtc.CompareTo(b.Interval.StartUtc));

        return new ViewerFreeBusyResult(
            ResourceRef:    resourceRef,
            ViewerActorId:  viewerActorId,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc:   windowEndUtc,
            FreeSlots:      free,
            BusyEvents:     projected);
    }

    /// <summary>
    /// The composed supply-side layer (Slice CALENDAR-LAYERS):
    /// <c>base_availability − exceptions[shared holidays + per-resource vacations/closures]</c>. Loads
    /// the resource's <see cref="ResourceAvailability"/> (its windows + its OWN single-day/spanning
    /// exceptions), resolves the SHARED-calendar holiday/closure days it subscribes to, and expands the
    /// availability with BOTH exception layers suppressed. Empty when the resource has no availability
    /// record.
    /// </summary>
    private async Task<List<TimeInterval>> ComposeAvailability(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct)
    {
        var availability = await _availabilityStore.GetAsync(tenantId, resourceRef, ct).ConfigureAwait(false);
        if (availability is null) return new List<TimeInterval>();

        // Resolve the shared-calendar holiday/closure days (the supply-side layer the resource does not
        // store itself — it only subscribes to the shared calendar). Widen the local-date span by ±1
        // day to match the availability expansion's tz-offset cushion.
        IReadOnlySet<DateOnly>? sharedExceptionDays = null;
        if (_sharedCalendarResolver is not null)
        {
            var localStart = DateOnly.FromDateTime(windowStartUtc.UtcDateTime).AddDays(-1);
            var localEnd = DateOnly.FromDateTime(windowEndUtc.UtcDateTime).AddDays(1);
            sharedExceptionDays = await _sharedCalendarResolver
                .ResolveSharedExceptionDaysAsync(tenantId, resourceRef, localStart, localEnd, ct)
                .ConfigureAwait(false);
        }

        // The resource's own exceptions (single-day + spanning) live on the availability and are applied
        // inside Expand; the shared-calendar days are passed as the additional suppression layer. Both
        // are whole-day removals applied at the local-date check BEFORE the wall-clock→UTC resolution —
        // the UTC invariant is preserved.
        var intervals = sharedExceptionDays is { Count: > 0 }
            ? _availabilityExpansion.Expand(availability, windowStartUtc, windowEndUtc, sharedExceptionDays)
            : _availabilityExpansion.Expand(availability, windowStartUtc, windowEndUtc);
        return intervals.ToList();
    }

    /// <summary>A busy occurrence paired with the event it came from (so the viewer projection can scope detail).</summary>
    private readonly record struct OccupiedInterval(TimeInterval Interval, CalendarEvent Event);

    /// <summary>
    /// Every busy UTC interval on <paramref name="resourceRef"/> in the window — the occurrence
    /// instants of every non-cancelled event where the resource is the headline
    /// <see cref="CalendarEvent.ResourceRef"/> or any <see cref="CalendarParticipation"/>. ALL
    /// occupancy classes count as busy (Bookable + Blocking + Tentative), regardless of the event's
    /// detail-visibility (a private appointment is still busy time). Each interval is paired with its
    /// source event so a viewer-scoped projection can redact detail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Occupancy uses the OCCUPIED interval, not the visible one (the padding slice).</b> Each
    /// occurrence's busy span is its <see cref="OccurrenceInstant.OccupiedInterval"/> —
    /// <c>[StartUtc − Padding.Pre, EndUtc + Padding.Post)</c> — so a 2:00–2:30 appointment with a 30-min
    /// post-padding blocks the resource until 3:00 (a 2:45 booking is rejected). With
    /// <see cref="EventPadding.None"/> (the default) the occupied interval equals the visible interval,
    /// reproducing the pre-padding behavior exactly. The padding is added to the <i>UTC instants</i>
    /// (which were already DST-resolved by <c>ExpandInstants</c> via <c>TimezoneResolver</c>) — a flat
    /// UTC duration, never wall-clock — so the load-bearing UTC invariant holds across DST. The widened
    /// occupied interval is then paired with its source event (the calendar-layers projection) so the
    /// viewer-scoped detail redaction can run over the SAME padded occupancy the booking gate sees.
    /// </para>
    /// </remarks>
    private async Task<List<OccupiedInterval>> GatherOccupancy(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct)
    {
        var events = await _eventStore.ListAsync(tenantId, ct).ConfigureAwait(false);
        var busy = new List<OccupiedInterval>();

        foreach (var ev in events)
        {
            // The resource is "on" the event when it is the headline resource ref OR a participant
            // (in any role). The single source of truth is the participation set; the headline ref is
            // a convenience that SetResource keeps mirrored as a Resource participation, but we check
            // both to be robust to an event that set only one.
            var onEvent = ev.ResourceRef == resourceRef
                || ev.Participations.Any(p => p.Participant == resourceRef);
            if (!onEvent) continue;

            // Cancelled whole event/series contributes nothing (ExpandInstants already returns empty,
            // but short-circuit for clarity).
            if (ev.Status == CalendarEventStatus.Cancelled) continue;

            // Expand over the window WIDENED by the padding envelope: an occurrence whose VISIBLE span
            // sits just outside the window but whose padded OCCUPIED span reaches into it must still be
            // counted busy (e.g. an appointment ending just before windowStart whose post-padding spills
            // into the window). ExpandInstants already keeps any instant overlapping the (widened)
            // window; we then occupy with the padded interval.
            var expandStart = windowStartUtc - ev.Padding.Post; // post-padding can pull an earlier event into the window
            var expandEnd = windowEndUtc + ev.Padding.Pre;       // pre-padding can pull a later event into the window

            foreach (var occ in _eventExpansion.ExpandInstants(ev, expandStart, expandEnd))
            {
                // Padding: the busy span is the padded OCCUPIED interval (visible span widened by
                // pre/post), never the raw visible one. Layers: pair it with its source event so the
                // viewer projection can redact private-appointment detail over the same occupancy.
                var occupied = occ.OccupiedInterval(ev.Padding);
                if (occupied.EndUtc <= occupied.StartUtc) continue; // a zero/negative span contributes nothing
                busy.Add(new OccupiedInterval(occupied, ev));
            }
        }

        return busy;
    }
}
