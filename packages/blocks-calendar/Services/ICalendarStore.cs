using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The store for owned <see cref="OwnedCalendar"/> collections (calendar-productization design note
/// #149, slice C1) — the demand-side grouping an event belongs to, tenant-scoped. Mirrors the
/// <see cref="ICalendarEventStore"/> discipline: a required <see cref="TenantId"/>, composite
/// <c>(TenantId, Id)</c> keying, and cross-tenant isolation (a store never returns another tenant's
/// calendars).
/// </summary>
/// <remarks>
/// C1 ships an in-memory implementation for the block (and block tests); the node host overrides it
/// with a durable EF-backed <c>NodeEfCalendarStore</c> over the recoverable SQLCipher store, exactly as
/// it did for <see cref="ICalendarEventStore"/>.
/// </remarks>
public interface ICalendarStore
{
    /// <summary>Insert or replace a calendar (upsert on the composite <c>(TenantId, Id)</c> key).</summary>
    Task SaveAsync(OwnedCalendar calendar, CancellationToken ct = default);

    /// <summary>Load a single calendar by id within a tenant; <see langword="null"/> if absent or owned by another tenant.</summary>
    Task<OwnedCalendar?> GetAsync(TenantId tenantId, CalendarId id, CancellationToken ct = default);

    /// <summary>List all calendars for a tenant, ordered so the default (if any) is first, then by created time.</summary>
    Task<IReadOnlyList<OwnedCalendar>> ListAsync(TenantId tenantId, CancellationToken ct = default);

    /// <summary>Remove a calendar by id within a tenant. Idempotent; returns true when a calendar was removed.</summary>
    Task<bool> RemoveAsync(TenantId tenantId, CalendarId id, CancellationToken ct = default);
}
