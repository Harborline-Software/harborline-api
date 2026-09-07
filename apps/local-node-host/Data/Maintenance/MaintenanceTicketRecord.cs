namespace Harborline.Api.LocalNodeHost.Data.Maintenance;

/// <summary>
/// Node-local-authoritative maintenance ticket (ADR 0115 D8 Stage 2; Admiral
/// ruling 2026-06-13 Option B). The flat doctype the Harborline maintenance
/// frontend consumes today (<c>MaintenanceTicket</c> in
/// the earlier desktop app's <c>src/api/erpnext.ts</c>, served until now by the
/// Bridge <c>/api/v1/erpnext/maintenance</c> proxy) — NOT the rich
/// <c>WorkOrder</c> state-machine aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Node-exclusive, NOT a shared <c>IHarborlineEntityModule</c>.</b> This entity
/// belongs to the embedded local node alone. It is mapped by
/// <see cref="NodeLocalMaintenanceDbContext"/>, which is a separate
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> from
/// <see cref="LocalNodeDbContext"/> (the shared-module financial store). Because
/// it never enters <see cref="LocalNodeDbContext"/>'s injected module set, the
/// council C2 both-provider model-drift arch-test
/// (<c>BothProviderModelDriftTests</c>) does not see it — the maintenance table
/// is correctly absent from the Bridge's Npgsql schema (maintenance has no
/// Bridge EF persistence; it was in-memory there too). This is the C2-safe
/// posture the ruling requires.
/// </para>
/// <para>
/// <b>Encrypted at rest (SC-1).</b> The record lives in the SAME
/// SQLCipher-encrypted database file as the financial store, keyed through the
/// same <see cref="SqlCipherConnectionInterceptor"/> (root-seed-derived DEK).
/// There is no plaintext path.
/// </para>
/// <para>
/// Field semantics mirror the flat ERPNext <c>Maintenance Ticket</c> doctype the
/// Bridge proxy projected: <c>name, subject, property, status, priority,
/// assigned_to, cost</c>. <see cref="Status"/> is one of
/// <c>Open | In Progress | Resolved | Closed</c>; <see cref="Priority"/> is one
/// of <c>Low | Medium | High | Critical</c> (validated at the route boundary,
/// stored as text so the contract stays wire-faithful).
/// </para>
/// </remarks>
public sealed class MaintenanceTicketRecord
{
    /// <summary>
    /// Stable document name / primary key. Generated server-side on create
    /// (ERPNext-style <c>TKT-{n}</c>) when the caller does not supply one.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>Short human-readable summary of the ticket.</summary>
    public required string Subject { get; set; }

    /// <summary>The property the ticket is filed against (free-text id today).</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>
    /// Lifecycle status — one of <c>Open | In Progress | Resolved | Closed</c>.
    /// Stored as text to stay faithful to the flat wire contract.
    /// </summary>
    public string Status { get; set; } = "Open";

    /// <summary>
    /// Priority — one of <c>Low | Medium | High | Critical</c>.
    /// </summary>
    public string Priority { get; set; } = "Medium";

    /// <summary>Optional assignee identifier.</summary>
    public string? AssignedTo { get; set; }

    /// <summary>Optional free-text description captured on create.</summary>
    public string? Description { get; set; }

    /// <summary>Optional resolution note captured on a resolving update.</summary>
    public string? Resolution { get; set; }

    /// <summary>Optional cost figure captured on update.</summary>
    public decimal? Cost { get; set; }

    /// <summary>Creation timestamp (UTC). Set server-side on create.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last-modified timestamp (UTC). Updated on every write.</summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
