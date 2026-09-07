using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The store for <see cref="SharedCalendar"/> (Slice CALENDAR-LAYERS) — the supply-side shared
/// calendars of availability exceptions (clinic holiday calendars). Keyed
/// <c>(TenantId, SharedCalendarId)</c>: one record per shared calendar (re-saving replaces it).
/// Tenant-scoped + cross-tenant isolated, exactly like <see cref="ICalendarEventStore"/> and
/// <see cref="IResourceAvailabilityStore"/>.
/// </summary>
public interface ISharedCalendarStore
{
    /// <summary>Insert or replace a shared calendar for its <c>(TenantId, SharedCalendarId)</c>.</summary>
    Task SaveAsync(SharedCalendar calendar, CancellationToken ct = default);

    /// <summary>
    /// Load a shared calendar within a tenant; <see langword="null"/> if none on file or it belongs to
    /// another tenant.
    /// </summary>
    Task<SharedCalendar?> GetAsync(TenantId tenantId, SharedCalendarId id, CancellationToken ct = default);

    /// <summary>List all shared calendars for a tenant.</summary>
    Task<IReadOnlyList<SharedCalendar>> ListAsync(TenantId tenantId, CancellationToken ct = default);

    /// <summary>Remove a shared calendar within a tenant. Idempotent; returns true when one was removed.</summary>
    Task<bool> RemoveAsync(TenantId tenantId, SharedCalendarId id, CancellationToken ct = default);
}
