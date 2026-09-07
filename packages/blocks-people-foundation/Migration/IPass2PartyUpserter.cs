using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

// Disambiguate from the producer-local legacy ImportOutcome<T> that also lives
// in this namespace. The Pass-2 upserter contract is the canonical A0 union.
using ImportOutcome = Harborline.Api.Foundation.Import.Outcomes.ImportOutcome<Harborline.Api.Blocks.People.Foundation.Models.Party>;

namespace Harborline.Api.Blocks.People.Foundation.Migration;

/// <summary>
/// Pass-2 (reference-data) ERPNext party upserter (ADR 0100 §4.2.3; importer
/// spec §4.2 sub-pass 3). Per-record upserters that return the canonical
/// <see cref="ImportOutcome{T}"/> discriminated union from
/// <c>Harborline.Api.Foundation.Import</c> (Workstream A0) so each outcome can be
/// recorded into an <see cref="Harborline.Api.Foundation.Import.Census.ImportCensus"/>
/// by the orchestrator (A7) — the record-census-conservation contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from the legacy <c>IErpnextPartyImporter</c>.</b> That importer
/// returns the producer-local <c>Harborline.Api.Blocks.People.Foundation.Migration.ImportOutcome&lt;T&gt;</c>
/// flat record. This Pass-2 upserter is the canonical census-conserving surface
/// the A6 reconcile + A7 orchestrator consume; the legacy importer is retained
/// (shrink-only allowlist) until its consumers migrate.
/// </para>
/// <para>
/// <b>PII discipline (ADR 0098 S1 / ADR 0100 C9).</b> The upserter NEVER logs
/// party names / emails / phones / tax-ids, and a reject carries only the
/// scalar-id-only <see cref="ImportFailure"/> (the ERPNext <c>name</c> opaque
/// key + DocType + bounded reason code + optional field NAME) — never a PII
/// value. The <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Rejected"/> arm carries no
/// <see cref="Party"/> by construction.
/// </para>
/// <para>
/// <b>External-ref tag convention</b> (matches the legacy importer +
/// blocks-work-projects): imported parties are tagged with
/// <c>externalRef:erpnext:customer:{Name}</c> (or <c>:supplier:{Name}</c>) for
/// the FK and <c>erpnextModified:{Modified}</c> for the version key, both on
/// <see cref="Party.Tags"/>. Contact + address resolution walks these tags.
/// </para>
/// </remarks>
public interface IPass2PartyUpserter
{
    /// <summary>
    /// Upsert an ERPNext Customer into the canonical <see cref="Party"/>
    /// substrate. Maps <c>customer_type</c> → <see cref="PartyKind"/>, attaches
    /// a <c>customer</c> role-edge, and best-effort-attaches the inline
    /// email/phone. Idempotent on <c>(Name, Modified)</c>.
    /// </summary>
    /// <returns>
    /// <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Inserted"/> / <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Updated"/> /
    /// <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Skipped"/> on success; <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Rejected"/>
    /// (with a scalar-only <see cref="ImportFailure"/>) when a required field is missing.
    /// </returns>
    Task<ImportOutcome> UpsertCustomerAsync(
        ErpnextPartyCustomerSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upsert an ERPNext Supplier — same shape as
    /// <see cref="UpsertCustomerAsync"/> but attaches a <c>vendor</c> role.
    /// </summary>
    Task<ImportOutcome> UpsertSupplierAsync(
        ErpnextPartySupplierSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attach an ERPNext Contact's email/phone to the party its
    /// <see cref="ErpnextContactSource.Links"/> resolve to (already imported in
    /// the Customer/Supplier sub-pass). An orphan contact (no resolvable link)
    /// is <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Rejected"/> with
    /// <see cref="ImportRejectReason.UnresolvedReference"/> (importer spec §4.2
    /// failure modes). Returns <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Updated"/> on a
    /// successful attach, <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Skipped"/> when the
    /// contact carries no shape-valid email or phone.
    /// </summary>
    Task<ImportOutcome> AttachContactAsync(
        ErpnextContactSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Append an ERPNext Address as a <c>PartyAddress</c> sub-entity on the
    /// party its <see cref="ErpnextAddressSource.Links"/> resolve to. An
    /// orphaned address (no resolvable owning party) is
    /// <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Rejected"/> with
    /// <see cref="ImportRejectReason.UnresolvedReference"/>. A structurally
    /// invalid address (missing required line/city/region/postal/country) is
    /// <see cref="Harborline.Api.Foundation.Import.Outcomes.ImportOutcome{T}.Rejected"/> with
    /// <see cref="ImportRejectReason.MissingRequiredField"/>.
    /// </summary>
    Task<ImportOutcome> AttachAddressAsync(
        ErpnextAddressSource source,
        TenantId tenantId,
        PartyId actor,
        CancellationToken cancellationToken = default);
}
