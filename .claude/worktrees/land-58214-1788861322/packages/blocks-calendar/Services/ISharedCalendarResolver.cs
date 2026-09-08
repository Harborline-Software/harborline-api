using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Resolves the <b>applicable shared-calendar holiday/closure days</b> for a resource (Slice
/// CALENDAR-LAYERS) — walks the resource's <see cref="CalendarSubscription"/>s, loads each subscribed
/// <see cref="SharedCalendar"/>, and unions its exception spans into the set of whole days the
/// resource is unavailable because of a SHARED (org/location) holiday. The supply-side composition
/// step that lets "one holiday entry → many resources" reduce every subscribed resource's free/busy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where this sits in the layered composition.</b> A resource's effective availability is
/// <c>(base_availability − exceptions[shared holidays + per-resource vacations/closures]) −
/// occupancy</c>. The per-resource exceptions (single-day + spanning) are applied by
/// <see cref="AvailabilityExpansionService"/> from the resource's own
/// <see cref="ResourceAvailability"/>; this resolver supplies the OTHER half — the shared-calendar
/// holidays, which the resource does not store itself (it only subscribes to the calendar). The
/// free/busy service intersects the resolved excepted-day set with the requested window and removes
/// those whole days from the expanded availability before differencing occupancy.
/// </para>
/// <para>
/// <b>Whole-day, tenant-scoped.</b> Shared-calendar exceptions are whole-day (a holiday / closure is
/// a whole-day removal, like an EXDATE). The resolver returns the <i>dates</i>; the caller suppresses
/// every availability interval whose local date is in the set. Cross-tenant isolated — only the
/// resource's own tenant's subscriptions + shared calendars are considered.
/// </para>
/// </remarks>
public interface ISharedCalendarResolver
{
    /// <summary>
    /// The set of whole days, within <c>[<paramref name="windowStartDate"/>,
    /// <paramref name="windowEndDate"/>]</c> (inclusive), on which <paramref name="resourceRef"/> is
    /// unavailable because a shared calendar it subscribes to marks the day a holiday / closure.
    /// Empty when the resource has no subscriptions or none of them except a day in the window.
    /// </summary>
    Task<IReadOnlySet<DateOnly>> ResolveSharedExceptionDaysAsync(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateOnly windowStartDate,
        DateOnly windowEndDate,
        CancellationToken ct = default);
}
