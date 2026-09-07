using System.Collections.Generic;

namespace Harborline.Api.Blocks.FinancialTax.Migration;

/// <summary>
/// ERPNext source row for the Pass-2 tax upserter (ADR 0100 §4.2.3; importer
/// spec §4.2 sub-pass 2). Maps an ERPNext
/// <c>Sales/Purchase Taxes and Charges Template</c> to a <c>TaxCode</c> +
/// its child <c>taxes</c> rows to a <c>TaxRate</c> fan-out, scoped to a
/// jurisdiction synthesized from the ERPNext <c>Tax Category</c>.
/// </summary>
/// <remarks>
/// The rate child-rows are a typed list (<see cref="Rates"/>) rather than the
/// legacy importer's inlined JSON string — cleaner to fixture and removes a
/// parse-failure surface. Tax records carry no PII.
/// </remarks>
/// <param name="Name">ERPNext <c>name</c> — stable id; the FK we dedupe on. Opaque, safe to log.</param>
/// <param name="Modified">ERPNext <c>modified</c> — version key (ordinal compare decides Skipped vs Updated).</param>
/// <param name="TaxName">Human-stable tax-code label (e.g. "VA Sales Tax").</param>
/// <param name="TaxCategory">Optional ERPNext <c>Tax Category</c> — synthesizes one <c>TaxJurisdiction</c> per unique category (best-effort; user reviews post-import).</param>
/// <param name="Rates">The template's <c>taxes</c> child rows.</param>
/// <param name="Disabled">When true, the local <c>TaxCode</c> is marked <c>IsActive = false</c>.</param>
public sealed record ErpnextTaxTemplateSource(
    string Name,
    string Modified,
    string TaxName,
    string? TaxCategory,
    IReadOnlyList<ErpnextTaxTemplateRateRow> Rates,
    bool Disabled = false);

/// <summary>One row of an ERPNext template's <c>taxes</c> child table.</summary>
/// <param name="AccountHead">ERPNext <c>account_head</c> — resolves to a local <c>GLAccount</c> via <c>IAccountResolver</c> (seeded by ledger Pass 1).</param>
/// <param name="Rate">Percentage rate, <c>[0,100]</c> (e.g. <c>5.3m</c> for 5.3%).</param>
/// <param name="IncludedInPrintRate">When true the rate is Inclusive (price-with-tax); else OnSubtotal.</param>
public sealed record ErpnextTaxTemplateRateRow(
    string AccountHead,
    decimal Rate,
    bool IncludedInPrintRate);
