namespace Harborline.Api.LocalNodeHost.Data.Leases;

/// <summary>
/// Node-local-authoritative lease (ADR 0115 D8 Stage 2 Cohort C). The flat
/// doctype the Harborline leases frontend consumes today (<c>Lease</c> in
/// the earlier desktop app's <c>src/api/erpnext.ts</c>, served until now by the Bridge
/// <c>/api/v1/erpnext/leases</c> proxy + cached in the Rust SQLite layer).
/// </summary>
/// <remarks>
/// <para>
/// <b>Node-exclusive, NOT a shared <c>IHarborlineEntityModule</c>.</b> Mapped by
/// <see cref="NodeLocalLeaseDbContext"/>, a separate
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> from
/// <see cref="LocalNodeDbContext"/>. Never enters the shared module set, so the
/// council C2 both-provider parity test does not see it. Mirrors
/// <c>NodeLocalMaintenanceDbContext</c>.
/// </para>
/// <para>
/// <b>Encrypted at rest (SC-1).</b> Same SQLCipher-encrypted file + interceptor
/// as the financial store; no plaintext path.
/// </para>
/// <para>
/// Field semantics mirror the flat ERPNext <c>Lease</c> doctype: <c>name, tenant,
/// property, unit, start_date, end_date, monthly_rent, status, company,
/// termCadence, autoRenew</c>. <see cref="Status"/> is one of <c>Active | Expired
/// | Terminated</c>; <see cref="TermCadence"/> is one of <c>daily | weekly |
/// monthly | multi-month | yearly | fixed</c> (validated at the route boundary).
/// Dates are stored as the wire-faithful ISO date strings the frontend sends
/// (the ERPNext doctype uses string dates), not parsed to <c>DateTimeOffset</c>,
/// so the round-trip is byte-exact for the existing UI.
/// </para>
/// </remarks>
public sealed class LeaseRecord
{
    /// <summary>Stable document name / primary key (ERPNext-style id).</summary>
    public required string Name { get; set; }

    /// <summary>Tenant identifier (free-text id, as in the ERPNext doctype).</summary>
    public string Tenant { get; set; } = string.Empty;

    /// <summary>The property this lease is filed against (free-text id).</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>The unit within the property.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Lease start date (ISO date string, wire-faithful).</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>
    /// Lease end date (ISO date string). For fixed leases the absolute end; for
    /// auto-renewing leases the current period boundary.
    /// </summary>
    public string EndDate { get; set; } = string.Empty;

    /// <summary>Monthly rent amount.</summary>
    public decimal MonthlyRent { get; set; }

    /// <summary>
    /// Lifecycle status — one of <c>Active | Expired | Terminated</c>. Stored as
    /// text to stay faithful to the flat wire contract.
    /// </summary>
    public string Status { get; set; } = "Active";

    /// <summary>The company / tenant scope (free-text id).</summary>
    public string Company { get; set; } = string.Empty;

    /// <summary>
    /// Term cadence for window sizing and renewal logic — one of <c>daily |
    /// weekly | monthly | multi-month | yearly | fixed</c>. Default <c>fixed</c>.
    /// </summary>
    public string TermCadence { get; set; } = "fixed";

    /// <summary>If true, the lease auto-renews at <see cref="EndDate"/>.</summary>
    public bool AutoRenew { get; set; }

    /// <summary>Creation timestamp (UTC). Set server-side on create.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last-modified timestamp (UTC). Updated on every write.</summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
