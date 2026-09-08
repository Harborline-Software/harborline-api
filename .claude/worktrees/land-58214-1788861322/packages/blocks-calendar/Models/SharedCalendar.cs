using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A <b>shared, supply-side calendar of availability exceptions</b> (Slice CALENDAR-LAYERS) — owned
/// by an org / location / group, holding holiday + closure date-spans that <b>REMOVE availability for
/// every resource subscribed to it</b>. The clinic holiday calendar: "the clinic is closed
/// Thanksgiving, Christmas, and for renovation 03-20 through 03-22" — one entry that reduces the
/// free/busy of every doctor and room in scope, without re-stating the holiday on each resource.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entries are availability EXCEPTIONS, not events.</b> A shared calendar holds whole-day
/// <see cref="ExceptionSpan"/>s (per the calendar-layers design: "holiday/closure = availability
/// EXCEPTION (supply-side) on a SHARED org/location calendar; one entry → many resources"). It does
/// NOT hold occupancy events — those are demand-side and per-resource. The free/busy composition
/// unions a resource's applicable shared-calendar exceptions with its own per-resource exceptions and
/// subtracts the union from the base availability supply, BEFORE differencing occupancy.
/// </para>
/// <para>
/// <b>One entry → many resources, via subscriptions.</b> A shared calendar is decoupled from the
/// resources it affects: the link is a <see cref="CalendarSubscription"/> (a
/// <c>(resource, sharedCalendar)</c> scope edge). The "which shared calendars apply to this resource"
/// resolution walks the subscriptions. Adding a holiday to the shared calendar reduces every
/// subscribed resource's availability with no per-resource edit; subscribing a new resource picks up
/// the whole calendar's history.
/// </para>
/// <para>
/// <b>Tenant-scoped + cross-tenant isolated.</b> Keyed <c>(TenantId, SharedCalendarId)</c> in the
/// store, exactly like the event + availability stores — a tenant-A holiday never reduces a tenant-B
/// resource's availability.
/// </para>
/// </remarks>
public sealed class SharedCalendar
{
    private readonly List<ExceptionSpan> _exceptions = new();

    private SharedCalendar(SharedCalendarId id, TenantId tenantId, string name)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
    }

    /// <summary>The shared calendar's stable identifier (the subscription target).</summary>
    public SharedCalendarId Id { get; }

    /// <summary>The tenant this shared calendar belongs to (cross-tenant isolated in the store).</summary>
    public TenantId TenantId { get; }

    /// <summary>A human-readable name ("Acme Clinic Holidays", "West-Wing Closures").</summary>
    public string Name { get; private set; }

    /// <summary>
    /// The whole-day availability-exception spans (holidays / closures) this calendar applies to every
    /// subscribed resource. Read-only; mutate via <see cref="AddException"/> /
    /// <see cref="RemoveException"/>.
    /// </summary>
    public IReadOnlyList<ExceptionSpan> Exceptions => _exceptions;

    /// <summary>
    /// Create a shared calendar named <paramref name="name"/> in <paramref name="tenantId"/>, with a
    /// fresh <see cref="SharedCalendarId"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static SharedCalendar Create(TenantId tenantId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Shared-calendar name must be non-empty.", nameof(name));
        return new SharedCalendar(SharedCalendarId.NewId(), tenantId, name);
    }

    /// <summary>Add a holiday / closure span (applies to every subscribed resource).</summary>
    public SharedCalendar AddException(ExceptionSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        _exceptions.Add(span);
        return this;
    }

    /// <summary>Remove a previously-added exception span (by value equality). Idempotent.</summary>
    public SharedCalendar RemoveException(ExceptionSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        _exceptions.RemoveAll(e => e == span);
        return this;
    }

    /// <summary>Rename the calendar.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public SharedCalendar Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Shared-calendar name must be non-empty.", nameof(name));
        Name = name;
        return this;
    }

    /// <summary>
    /// True when <paramref name="date"/> falls inside any of this calendar's exception spans — the
    /// day is a holiday / closure for every subscribed resource.
    /// </summary>
    public bool IsExcepted(DateOnly date) => _exceptions.Any(e => e.Contains(date));

    /// <summary>
    /// Rehydrate from persisted state — used by the store to reconstruct the calendar without
    /// re-running the factory (preserving the id). The store is the only caller.
    /// </summary>
    public static SharedCalendar Rehydrate(
        SharedCalendarId id,
        TenantId tenantId,
        string name,
        IEnumerable<ExceptionSpan> exceptions)
    {
        var sc = new SharedCalendar(id, tenantId, name);
        foreach (var e in exceptions) sc._exceptions.Add(e);
        return sc;
    }
}
