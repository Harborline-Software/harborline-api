using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// The recurring <b>bookable supply</b> for one schedulable resource (a Party — a doctor — or an
/// Asset — a room) within a tenant (Slice S3). "Dr. Smith Mon–Fri 9–5"; "this worker is available
/// for shifts Sat–Sun". A <see cref="ResourceAvailability"/> is a set of recurring
/// <see cref="AvailabilityWindow"/>s plus an EXDATE-style set of <see cref="ExceptionDates"/>
/// (whole days the resource is unavailable — a holiday, a day off). Free/busy differences this supply
/// against the resource's occupancy: <c>free = availability − ALL occupancy (Bookable + Blocking)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resource is a <see cref="ParticipantRef"/></b> — exactly the booking core's
/// schedulable-resource ref (Party or Asset), so availability and bookings speak the same id.
/// Keyed <c>(TenantId, ResourceRef)</c> in the store: one availability record per resource per tenant
/// (re-saving replaces it). Tenant-scoped + cross-tenant isolated like the event store.
/// </para>
/// <para>
/// <b>Exceptions = whole-day removals.</b> A resource's holiday is an <see cref="ExceptionDates"/>
/// entry — every window on that date is suppressed when the supply is expanded. (A partial-day block,
/// e.g. lunch, is modeled instead as a <see cref="Occupancy.Blocking"/> event, which free/busy
/// subtracts as occupancy — so both "the doctor takes Friday off" and "the doctor's daily lunch"
/// correctly remove time from free/busy, via the two complementary mechanisms.)
/// </para>
/// </remarks>
public sealed class ResourceAvailability
{
    private readonly List<AvailabilityWindow> _windows = new();
    private readonly SortedSet<DateOnly> _exceptionDates = new();
    private readonly List<ExceptionSpan> _exceptionSpans = new();

    private ResourceAvailability(TenantId tenantId, ParticipantRef resourceRef, string timezone)
    {
        TenantId = tenantId;
        ResourceRef = resourceRef;
        Timezone = timezone;
    }

    /// <summary>The tenant this availability belongs to (cross-tenant isolated in the store).</summary>
    public TenantId TenantId { get; }

    /// <summary>The schedulable resource (a Party or an Asset) this availability is the supply for.</summary>
    public ParticipantRef ResourceRef { get; }

    /// <summary>
    /// The IANA timezone the windows' wall-clock times are interpreted in (e.g.
    /// <c>America/Los_Angeles</c>). Applied via <c>TimezoneResolver</c> when the supply is expanded to
    /// UTC intervals — DST-aware, consistent with the event-occurrence path it is differenced against.
    /// </summary>
    public string Timezone { get; }

    /// <summary>The recurring bookable windows. Read-only; mutate via <see cref="AddWindow"/>.</summary>
    public IReadOnlyList<AvailabilityWindow> Windows => _windows;

    /// <summary>
    /// Whole days the resource is unavailable (EXDATE-style — a holiday / day off). Every window on an
    /// exception date is suppressed. Read-only; mutate via <see cref="AddException"/> /
    /// <see cref="RemoveException"/>.
    /// </summary>
    public IReadOnlyCollection<DateOnly> ExceptionDates => _exceptionDates;

    /// <summary>
    /// Per-resource <b>spanning</b> availability exceptions (Slice CALENDAR-LAYERS) — vacations /
    /// closures over a DATE SPAN. The supply-side generalization of the single-day
    /// <see cref="ExceptionDates"/>: "the doctor is on vacation Mon 03-09 through Fri 03-13" removes
    /// every window on every day in the span. Read-only; mutate via <see cref="AddExceptionSpan"/> /
    /// <see cref="RemoveExceptionSpan"/>. (A whole-day single-date exception still uses
    /// <see cref="ExceptionDates"/>; both are suppressed identically when the supply is expanded.)
    /// </summary>
    public IReadOnlyList<ExceptionSpan> ExceptionSpans => _exceptionSpans;

    /// <summary>
    /// True when <paramref name="date"/> is suppressed by this resource's OWN exceptions — either a
    /// single-day <see cref="ExceptionDates"/> entry or a day inside an <see cref="ExceptionSpans"/>
    /// span. (Shared-calendar holidays are composed separately by the free/busy service; this is the
    /// resource's own supply-side exceptions only.)
    /// </summary>
    public bool IsExcepted(DateOnly date)
        => _exceptionDates.Contains(date) || _exceptionSpans.Any(s => s.Contains(date));

    /// <summary>
    /// Create an availability record for <paramref name="resourceRef"/> in
    /// <paramref name="tenantId"/>, with its windows' times interpreted in
    /// <paramref name="timezone"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="timezone"/> is empty.</exception>
    public static ResourceAvailability Create(TenantId tenantId, ParticipantRef resourceRef, string timezone)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        if (string.IsNullOrWhiteSpace(timezone))
            throw new ArgumentException("Timezone must be non-empty (IANA tz id).", nameof(timezone));
        return new ResourceAvailability(tenantId, resourceRef, timezone);
    }

    /// <summary>Add a recurring (or single-day) bookable window to the supply.</summary>
    public ResourceAvailability AddWindow(AvailabilityWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _windows.Add(window);
        return this;
    }

    /// <summary>Mark a whole day as unavailable (a holiday / day off). Idempotent.</summary>
    public ResourceAvailability AddException(DateOnly date)
    {
        _exceptionDates.Add(date);
        return this;
    }

    /// <summary>Un-mark a previously excepted day. Idempotent.</summary>
    public ResourceAvailability RemoveException(DateOnly date)
    {
        _exceptionDates.Remove(date);
        return this;
    }

    /// <summary>
    /// Add a spanning availability exception (a vacation / closure over a date span — Slice
    /// CALENDAR-LAYERS). Every window on every day in the span is suppressed.
    /// </summary>
    public ResourceAvailability AddExceptionSpan(ExceptionSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        _exceptionSpans.Add(span);
        return this;
    }

    /// <summary>Remove a previously-added spanning exception (by value equality). Idempotent.</summary>
    public ResourceAvailability RemoveExceptionSpan(ExceptionSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        _exceptionSpans.RemoveAll(s => s == span);
        return this;
    }

    /// <summary>
    /// Rehydrate from persisted state — used by the store to reconstruct the record (windows +
    /// single-day exceptions + spanning exceptions) without re-running the factory. The store is the
    /// only caller. <paramref name="exceptionSpans"/> is optional so an older S3 snapshot (which has
    /// no spanning exceptions) still rehydrates to an empty span set.
    /// </summary>
    public static ResourceAvailability Rehydrate(
        TenantId tenantId,
        ParticipantRef resourceRef,
        string timezone,
        IEnumerable<AvailabilityWindow> windows,
        IEnumerable<DateOnly> exceptionDates,
        IEnumerable<ExceptionSpan>? exceptionSpans = null)
    {
        var ra = new ResourceAvailability(tenantId, resourceRef, timezone);
        foreach (var w in windows) ra._windows.Add(w);
        foreach (var d in exceptionDates) ra._exceptionDates.Add(d);
        if (exceptionSpans is not null)
            foreach (var s in exceptionSpans) ra._exceptionSpans.Add(s);
        return ra;
    }
}
