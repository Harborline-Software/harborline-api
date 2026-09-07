using System.Collections.Generic;

namespace Harborline.Api.Blocks.Migration.Erpnext.Extraction;

/// <summary>
/// The one authoritative, versioned, fail-loud DocType mapping (ADR 0100 C5) —
/// pins each supported ERPNext <c>tab&lt;DocType&gt;</c> to the <c>Erpnext*Source</c>
/// DTO family it maps to, and the framework/system DocTypes the importer
/// deliberately ignores. Versioned with the ERPNext app version the dump came
/// from so a future ERPNext-schema change is a DELIBERATE map revision, not a
/// silent drift.
/// </summary>
/// <remarks>
/// <para>
/// This is the SHAPE map (DocType -> DTO family). The SEMANTIC maps
/// (<c>account_type</c> -> <c>GLAccountType</c>, <c>voucher_type</c> -> entry kind,
/// etc.) live in the per-pass upserters (A1+), which already own them — A0 does
/// not duplicate them.
/// </para>
/// <para>
/// Pinned to <see cref="ErpnextVersion"/> = ERPNext v15 (CIC-resolved 2026-05-29).
/// The <c>tab&lt;DocType&gt;</c> table-naming convention is stable across ERPNext
/// v13–v15; the exact column sets are version-sensitive, so this map is pinned and
/// the column reads in <see cref="MariaDbDumpExtractor"/> target the v15 shape.
/// </para>
/// <para>
/// A DocType present in the dump but absent from <see cref="MappedDocTypes"/> AND
/// absent from <see cref="KnownIrrelevantDocTypes"/> is classified
/// <see cref="ErpnextDocTypeClassification.UnmappedUnknown"/> — COUNTED + LISTED in
/// the report's <c>_unmapped/</c> section (C5), never silently dropped. This is how
/// a custom <c>tabProperty</c>/<c>tabLease</c>/<c>tabUnit</c> surfaces for CIC
/// review at RUN time (the property model is MIXED/unknown per the directive — NOT
/// hard-assumed here).
/// </para>
/// </remarks>
public static class ErpnextDocTypeMap
{
    /// <summary>The ERPNext app major version this map is pinned to (CIC-resolved).</summary>
    public const string ErpnextVersion = "v15";

    /// <summary>
    /// The supported DocTypes mapped to an <c>Erpnext*Source</c> DTO family, keyed by
    /// the ERPNext DocType name (the <c>tab*</c> table with the prefix stripped). The
    /// value is the target DTO family (one logical <c>Erpnext*Source</c> shape).
    /// </summary>
    public static IReadOnlyDictionary<string, ErpnextDocTypeTarget> MappedDocTypes { get; } =
        new Dictionary<string, ErpnextDocTypeTarget>(StringComparer.OrdinalIgnoreCase)
        {
            // Pass-1 chart.
            ["Account"]      = ErpnextDocTypeTarget.Account,
            ["Cost Center"]  = ErpnextDocTypeTarget.CostCenter,
            // Pass-2 reference data.
            ["Fiscal Year"]  = ErpnextDocTypeTarget.FiscalYear,
            ["Customer"]     = ErpnextDocTypeTarget.PartyCustomer,
            ["Supplier"]     = ErpnextDocTypeTarget.PartySupplier,
            ["Contact"]      = ErpnextDocTypeTarget.Contact,
            ["Address"]      = ErpnextDocTypeTarget.Address,
            ["Sales Taxes and Charges Template"]    = ErpnextDocTypeTarget.TaxTemplate,
            ["Purchase Taxes and Charges Template"] = ErpnextDocTypeTarget.TaxTemplate,
            // Pass-3/4 transactional.
            ["Journal Entry"]    = ErpnextDocTypeTarget.JournalEntry,
            ["Sales Invoice"]    = ErpnextDocTypeTarget.SalesInvoice,
            ["Purchase Invoice"] = ErpnextDocTypeTarget.PurchaseInvoice,
            ["Payment Entry"]    = ErpnextDocTypeTarget.PaymentEntry,
        };

    /// <summary>
    /// Child <c>tab*</c> tables consumed via in-process JOIN by their PARENT DocType's
    /// extractor — NOT independently extracted (so they are NOT
    /// <see cref="ErpnextDocTypeClassification.UnmappedUnknown"/>). The census treats a
    /// child table as <see cref="ErpnextDocTypeClassification.KnownIrrelevant"/> because
    /// its rows are accounted for under their parent.
    /// </summary>
    public static IReadOnlySet<string> ChildTableDocTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Journal Entry Account",       // -> ErpnextJournalEntryLineSource
            "Sales Invoice Item",          // -> ErpnextSalesInvoiceItem
            "Purchase Invoice Item",       // -> ErpnextPurchaseInvoiceItem
            "Sales Taxes and Charges",     // -> ErpnextTaxTemplateRateRow
            "Purchase Taxes and Charges",  // -> ErpnextTaxTemplateRateRow
            "Dynamic Link",                // -> ErpnextDynamicLink (Contact/Address party links)
            "Fiscal Year Company",         // -> CompanyShortName on ErpnextFiscalYearSource
            "Payment Entry Reference",     // -> payment allocations (A4.3 RUN-time; not on the v1 DTO)
        };

    /// <summary>
    /// ERPNext / Frappe FRAMEWORK + SYSTEM DocTypes the importer deliberately ignores —
    /// these are NOT financial-or-business data, so listing them keeps the report's
    /// <c>_unmapped/</c> section signal-rich. NOT exhaustive of every framework table
    /// (a full ERPNext install has hundreds) — it covers the high-frequency noise; any
    /// framework table not on this list still surfaces as
    /// <see cref="ErpnextDocTypeClassification.UnmappedUnknown"/> (visible, harmless) so
    /// the allowlist can only ever GROW deliberately, never silently swallow real data.
    /// </summary>
    public static IReadOnlySet<string> KnownIrrelevantDocTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Metadata / schema.
            "DocType", "DocField", "DocPerm", "DocType Link", "DocType Action",
            "Custom Field", "Property Setter", "Customize Form",
            // Naming / sequences.
            "Series", "Singles",
            // Versioning / audit / activity.
            "Version", "Activity Log", "Comment", "View Log", "Access Log",
            "Route History", "Energy Point Log",
            // Auth / session / users / permissions.
            "User", "Session Default", "Sessions", "User Permission", "Role",
            "Role Profile", "Has Role", "DefaultValue", "User Type",
            // Background jobs / scheduling / email queue.
            "Scheduled Job Type", "Scheduled Job Log", "Background Jobs",
            "Email Queue", "Email Queue Recipient", "Notification Log", "Error Log",
            "Error Snapshot", "Deleted Document",
            // Files / search / preferences.
            "File", "Global Search", "List View Settings", "Workspace",
            "Dashboard", "Number Card", "Onboarding Step",
        };

    /// <summary>
    /// Classifies a DocType present in the source into its census bucket (C5):
    /// mapped, known-irrelevant (framework/system OR a child table), or
    /// unmapped-unknown (surfaced to the <c>_unmapped/</c> section for CIC review).
    /// </summary>
    /// <param name="docType">The ERPNext DocType name (e.g. "Account", "Property").</param>
    public static ErpnextDocTypeClassification Classify(string docType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docType);

        if (MappedDocTypes.ContainsKey(docType))
        {
            return ErpnextDocTypeClassification.Mapped;
        }

        if (ChildTableDocTypes.Contains(docType) || KnownIrrelevantDocTypes.Contains(docType))
        {
            return ErpnextDocTypeClassification.KnownIrrelevant;
        }

        return ErpnextDocTypeClassification.UnmappedUnknown;
    }
}

/// <summary>
/// The <c>Erpnext*Source</c> DTO family a mapped DocType extracts into — the value
/// of <see cref="ErpnextDocTypeMap.MappedDocTypes"/>.
/// </summary>
public enum ErpnextDocTypeTarget
{
    /// <summary><c>tabAccount</c> -> <c>ErpnextAccountSource</c>.</summary>
    Account,

    /// <summary><c>tabCost Center</c> -> <c>ErpnextCostCenterSource</c>.</summary>
    CostCenter,

    /// <summary><c>tabFiscal Year</c> -> <c>ErpnextFiscalYearSource</c>.</summary>
    FiscalYear,

    /// <summary><c>tabCustomer</c> -> <c>ErpnextPartyCustomerSource</c>.</summary>
    PartyCustomer,

    /// <summary><c>tabSupplier</c> -> <c>ErpnextPartySupplierSource</c>.</summary>
    PartySupplier,

    /// <summary><c>tabContact</c> (+ <c>tabDynamic Link</c>) -> <c>ErpnextContactSource</c>.</summary>
    Contact,

    /// <summary><c>tabAddress</c> (+ <c>tabDynamic Link</c>) -> <c>ErpnextAddressSource</c>.</summary>
    Address,

    /// <summary>Tax templates (+ child rate rows) -> <c>ErpnextTaxTemplateSource</c>.</summary>
    TaxTemplate,

    /// <summary><c>tabJournal Entry</c> (+ child accounts) -> <c>ErpnextJournalEntrySource</c>.</summary>
    JournalEntry,

    /// <summary><c>tabSales Invoice</c> (+ child items) -> <c>ErpnextSalesInvoiceSource</c>.</summary>
    SalesInvoice,

    /// <summary><c>tabPurchase Invoice</c> (+ child items) -> <c>ErpnextPurchaseInvoiceSource</c>.</summary>
    PurchaseInvoice,

    /// <summary><c>tabPayment Entry</c> -> <c>ErpnextPaymentSource</c>.</summary>
    PaymentEntry,
}
