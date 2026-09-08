using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The series/occurrence store for <see cref="CalendarEvent"/> — persists the series master plus
/// its EXDATE set and RECURRENCE-ID overrides, tenant-scoped.
/// </summary>
/// <remarks>
/// <para>
/// This is the durable home of the temporal core (Slice S0). It round-trips the full entity —
/// including the EXDATE list and the override store — so a saved series re-loads with its
/// occurrence-level edits intact.
/// </para>
/// <para>
/// All reads are tenant-scoped: a <see cref="TenantId"/> is required and the store never returns
/// another tenant's events (composite <c>(TenantId, Id)</c> keying — the fleet financial-master
/// pattern). S0 ships an in-memory implementation; a durable EF-backed implementation is wired in
/// the local-node-host in a later step.
/// </para>
/// </remarks>
public interface ICalendarEventStore
{
    /// <summary>Insert or replace a calendar event (the series master + its EXDATE/overrides).</summary>
    Task SaveAsync(CalendarEvent calendarEvent, CancellationToken ct = default);

    /// <summary>Load a single event by id within a tenant; <see langword="null"/> if absent or owned by another tenant.</summary>
    Task<CalendarEvent?> GetAsync(TenantId tenantId, CalendarEventId id, CancellationToken ct = default);

    /// <summary>List all (non-deleted) events for a tenant.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListAsync(TenantId tenantId, CancellationToken ct = default);

    /// <summary>Remove an event by id within a tenant. Idempotent; returns true when an event was removed.</summary>
    Task<bool> RemoveAsync(TenantId tenantId, CalendarEventId id, CancellationToken ct = default);
}
