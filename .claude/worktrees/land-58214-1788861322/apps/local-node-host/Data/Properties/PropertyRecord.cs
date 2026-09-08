namespace Harborline.Api.LocalNodeHost.Data.Properties;

/// <summary>
/// Node-local-authoritative property (ADR 0115 D8 Stage 2 Cohort C). The flat
/// doctype the Harborline properties frontend consumes today (<c>Property</c> in
/// the earlier desktop app's <c>src/api/erpnext.ts</c>, served until now by the Bridge
/// <c>/api/v1/erpnext/properties</c> proxy + cached in the Rust SQLite layer) —
/// NOT a rich domain aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Node-exclusive, NOT a shared <c>IHarborlineEntityModule</c>.</b> This entity
/// belongs to the embedded local node alone. It is mapped by
/// <see cref="NodeLocalPropertyDbContext"/>, a separate
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> from
/// <see cref="LocalNodeDbContext"/> (the shared-module financial store). Because
/// it never enters <see cref="LocalNodeDbContext"/>'s injected module set, the
/// council C2 both-provider model-drift arch-test
/// (<c>BothProviderModelDriftTests</c>) does not see it — properties have no
/// Bridge EF persistence (they were ERPNext-imported + Rust-cached, never an
/// Npgsql entity). This mirrors the C2-safe posture of
/// <c>NodeLocalMaintenanceDbContext</c>.
/// </para>
/// <para>
/// <b>Encrypted at rest (SC-1).</b> The record lives in the SAME
/// SQLCipher-encrypted database file as the financial store, keyed through the
/// same <see cref="SqlCipherConnectionInterceptor"/> (root-seed-derived DEK or
/// SC-4 injected Store DEK). There is no plaintext path.
/// </para>
/// <para>
/// Field semantics mirror the flat ERPNext <c>Property</c> doctype the Bridge
/// proxy projected: <c>name, property_name, address_line_1, city, state,
/// postal_code, units, status, company</c>. <see cref="Status"/> is one of
/// <c>Active | Vacant | Maintenance | Sold</c> (validated at the route boundary,
/// stored as text so the contract stays wire-faithful).
/// </para>
/// </remarks>
public sealed class PropertyRecord
{
    /// <summary>Stable document name / primary key (ERPNext-style id).</summary>
    public required string Name { get; set; }

    /// <summary>Human-readable property name.</summary>
    public required string PropertyName { get; set; }

    /// <summary>First address line.</summary>
    public string AddressLine1 { get; set; } = string.Empty;

    /// <summary>City.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>State / region.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Postal code.</summary>
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>Number of units in the property.</summary>
    public int Units { get; set; }

    /// <summary>
    /// Lifecycle status — one of <c>Active | Vacant | Maintenance | Sold</c>.
    /// Stored as text to stay faithful to the flat wire contract.
    /// </summary>
    public string Status { get; set; } = "Active";

    /// <summary>The company / tenant scope (free-text id, as in the ERPNext doctype).</summary>
    public string Company { get; set; } = string.Empty;

    /// <summary>Creation timestamp (UTC). Set server-side on create.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last-modified timestamp (UTC). Updated on every write.</summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
