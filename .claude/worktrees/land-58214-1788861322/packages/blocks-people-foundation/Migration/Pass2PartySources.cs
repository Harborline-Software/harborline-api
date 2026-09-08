using System.Collections.Generic;

namespace Harborline.Api.Blocks.People.Foundation.Migration;

/// <summary>
/// ERPNext <c>Customer</c> doctype source record for the Pass-2 party upserter
/// (ADR 0100 §4.2.3; importer spec §4.2 sub-pass 3). Field shape matches the
/// Frappe DocType verbatim so the upserter can be fed from a
/// <see cref="Harborline.Api.Foundation.Import.Extraction.SourceRow"/> mapping or a
/// Frappe REST client.
/// </summary>
/// <remarks>
/// PII-bearing (<see cref="EmailId"/> / <see cref="MobileNo"/> / display names).
/// The upserter NEVER logs these and NEVER places them in the reject channel
/// (the channel is structurally scalar-id-only by construction — ADR 0098 S1 /
/// ADR 0100 C9). The opaque <see cref="Name"/> natural key is the only field
/// the upserter surfaces on a reject.
/// </remarks>
/// <param name="Name">ERPNext <c>name</c> — the stable id; the FK we dedupe on (e.g. <c>"CUST-0001"</c>). Opaque, safe to log.</param>
/// <param name="Modified">ERPNext <c>modified</c> — version key; ordinal compare decides Skipped vs Updated.</param>
/// <param name="CustomerName">Human-readable display name (PII; e.g. <c>"Acme Holdings LLC"</c>).</param>
/// <param name="CustomerType">ERPNext <c>customer_type</c> — <c>"Individual"</c> or <c>"Company"</c>; maps to <see cref="Models.PartyKind"/>.</param>
/// <param name="EmailId">Primary email (PII); optional. Imported as an <c>EmailAddress</c> row if shape-valid.</param>
/// <param name="MobileNo">Primary mobile (E.164 expected; PII); optional. Imported as a <c>PhoneNumber</c> row if shape-valid.</param>
/// <param name="TaxId">ERPNext <c>tax_id</c> (PII); optional. Stored unencrypted in v1 — see Party.TaxId.</param>
/// <param name="Disabled">When true, the imported Party gets <c>DoNotContact = true</c> as a soft-deactivation signal.</param>
public sealed record ErpnextPartyCustomerSource(
    string Name,
    string Modified,
    string CustomerName,
    string? CustomerType = null,
    string? EmailId = null,
    string? MobileNo = null,
    string? TaxId = null,
    bool Disabled = false);

/// <summary>
/// ERPNext <c>Supplier</c> doctype source record — mirrors
/// <see cref="ErpnextPartyCustomerSource"/> with field renames; the upserter
/// maps both onto the same <see cref="Models.Party"/> with a different role-edge.
/// </summary>
/// <param name="Name">ERPNext <c>name</c> — stable id (e.g. <c>"SUP-0001"</c>). Opaque, safe to log.</param>
/// <param name="Modified">ERPNext <c>modified</c> — version key.</param>
/// <param name="SupplierName">Human-readable display name (PII).</param>
/// <param name="SupplierType">ERPNext <c>supplier_type</c> — <c>"Individual"</c> or <c>"Company"</c>.</param>
/// <param name="EmailId">Primary email (PII); optional.</param>
/// <param name="MobileNo">Primary mobile (E.164 expected; PII); optional.</param>
/// <param name="TaxId">ERPNext <c>tax_id</c> (PII); optional.</param>
/// <param name="Disabled">Soft-deactivation flag.</param>
public sealed record ErpnextPartySupplierSource(
    string Name,
    string Modified,
    string SupplierName,
    string? SupplierType = null,
    string? EmailId = null,
    string? MobileNo = null,
    string? TaxId = null,
    bool Disabled = false);

/// <summary>
/// One row of an ERPNext <c>Dynamic Link</c> child table (the <c>links</c>
/// array on <c>Contact</c> / <c>Address</c>). Used to attach a contact or
/// address to its owning party.
/// </summary>
/// <param name="LinkDocType">ERPNext <c>link_doctype</c> — e.g. <c>"Customer"</c> or <c>"Supplier"</c>.</param>
/// <param name="LinkName">ERPNext <c>link_name</c> — the linked doctype's <c>name</c> (e.g. <c>"CUST-0001"</c>).</param>
public sealed record ErpnextDynamicLink(string LinkDocType, string LinkName);

/// <summary>
/// ERPNext <c>Contact</c> doctype source record. A contact carries one or more
/// <see cref="Links"/> to the owning party (Customer/Supplier); the upserter
/// attaches the contact's email/phone to the resolved Party as additional
/// <c>EmailAddress</c>/<c>PhoneNumber</c> sub-entities. An orphan contact (no
/// resolvable link) is rejected to the bin (importer spec §4.2 failure modes).
/// </summary>
/// <param name="Name">ERPNext <c>name</c> — opaque stable id, safe to log.</param>
/// <param name="EmailId">Contact email (PII); optional.</param>
/// <param name="MobileNo">Contact phone (E.164 expected; PII); optional.</param>
/// <param name="Links">Dynamic-link rows pointing at the owning Customer/Supplier.</param>
public sealed record ErpnextContactSource(
    string Name,
    string? EmailId,
    string? MobileNo,
    IReadOnlyList<ErpnextDynamicLink> Links)
{
    /// <summary>Empty-links convenience for orphan-contact fixtures.</summary>
    public ErpnextContactSource(string name, string? emailId, string? mobileNo)
        : this(name, emailId, mobileNo, System.Array.Empty<ErpnextDynamicLink>())
    {
    }
}

/// <summary>
/// ERPNext <c>Address</c> doctype source record. Walks <see cref="Links"/> to
/// find the owning party and appends a <c>PartyAddress</c> sub-entity.
/// </summary>
/// <param name="Name">ERPNext <c>name</c> — opaque stable id, safe to log.</param>
/// <param name="AddressLine1">Street line 1 (PII); required.</param>
/// <param name="City">City (PII); required.</param>
/// <param name="State">State / region (PII); required.</param>
/// <param name="Pincode">Postal code (PII); required.</param>
/// <param name="Country">Country — ERPNext stores the full country name; the upserter maps to ISO 3166-1 alpha-2.</param>
/// <param name="AddressLine2">Optional street line 2 (PII).</param>
/// <param name="IsPrimaryAddress">ERPNext <c>is_primary_address</c> — sets <c>PartyAddress.IsPrimary</c>.</param>
/// <param name="Links">Dynamic-link rows pointing at the owning Customer/Supplier.</param>
public sealed record ErpnextAddressSource(
    string Name,
    string AddressLine1,
    string City,
    string State,
    string Pincode,
    string Country,
    string? AddressLine2,
    bool IsPrimaryAddress,
    IReadOnlyList<ErpnextDynamicLink> Links);
