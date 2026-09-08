namespace Harborline.Api.Blocks.Leases.Services;

/// <summary>
/// Source DTO for an ERPNext Lease / Rent Contract entry (PM-pack import extension,
/// ADR 0120 PR-D).
///
/// <para>
/// ERPNext PM installations commonly store tenancy contracts in a custom DocType
/// (e.g. <c>tabRent Contract</c> or <c>tabLease</c>). This record captures the
/// fields the <see cref="ErpNextLeaseCaptureService"/> needs to write a
/// <see cref="Harborline.Api.Blocks.Leases.Models.LeaseSubLedgerLink"/> without touching
/// any financial-cluster type.
/// </para>
///
/// <para>
/// <b>D3 pack-boundary (ADR 0111):</b> this record lives in the PM pack
/// (<c>blocks-leases</c>). It is intentionally minimal — it carries only the ids
/// the capture service needs; it does NOT expose any ERPNext financial-doctype fields
/// (no <c>GrandTotal</c>, no <c>ARAccountId</c>, etc.).
/// </para>
/// </summary>
/// <param name="ErpNextName">
/// ERPNext <c>name</c> field (stable, unique within the source): e.g. <c>"RC-00001"</c>.
/// Used as the idempotency key — re-importing the same record is a no-op.
/// </param>
/// <param name="Modified">
/// ERPNext <c>modified</c> timestamp string. Opaque version key; stored for reconciliation
/// provenance. Not parsed — ordinal equality only.
/// </param>
/// <param name="Customer">
/// ERPNext <c>customer</c> (or party) name. The capture service does NOT resolve this
/// to a canonical <see cref="Harborline.Api.Blocks.People.Foundation.Models.PartyId"/> — the
/// caller resolves the party and passes it separately (ADR 0100 C6).
/// </param>
/// <param name="SalesInvoiceRef">
/// Optional ERPNext <c>Sales Invoice</c> name that links this contract to its first
/// invoice. Used for cross-referencing the sub-ledger account minted by the generic
/// AR-import pipeline. Null when no linked invoice exists in the source (deferred
/// billing or pre-activation contract).
/// </param>
public sealed record ErpNextLeaseContractSource(
    string ErpNextName,
    string Modified,
    string? Customer,
    string? SalesInvoiceRef);
