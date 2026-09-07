using FL = Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialTax.Models;
using Harborline.Api.Blocks.FinancialTax.Services;
using Harborline.Api.Foundation.Import.Outcomes;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialTax.Migration;

/// <summary>
/// Default <see cref="IPass2TaxUpserter"/>. Reuses <see cref="ITaxCodeStore"/> +
/// <see cref="ITaxRateLookup"/> + <see cref="ITaxJurisdictionStore"/> +
/// <see cref="IAccountResolver"/>; holds no state of its own and recreates no
/// domain logic. Returns the canonical A0 <see cref="ImportOutcome{T}"/> union.
/// </summary>
/// <remarks>
/// Tax records carry no PII. The cross-system FK is the
/// <c>externalRef:&lt;name&gt;|modified:&lt;modified&gt;</c> marker on
/// <see cref="TaxCode.Notes"/> until a dedicated <c>ExternalRef</c> field lands.
/// A rate row whose <c>account_head</c> does not resolve is dropped (Pass 1
/// should have seeded it); a template with no resolvable rows still imports the
/// <see cref="TaxCode"/> (flagged in the reconcile report).
/// </remarks>
public sealed class Pass2TaxUpserter : IPass2TaxUpserter
{
    private const string TaxDocType = "Sales Taxes and Charges Template";
    private const string ExternalRefPrefix = "externalRef:";
    private const string ModifiedMarker = "|modified:";

    /// <summary>Effective-date floor covering history (importer spec §4.2; user refines post-import).</summary>
    private static readonly DateOnly HistoryEffectiveDate = new(2000, 1, 1);

    private readonly ITaxCodeStore _codes;
    private readonly ITaxRateLookup _rates;
    private readonly ITaxJurisdictionStore _jurisdictions;
    private readonly IAccountResolver _accounts;
    private readonly TimeProvider _time;

    /// <summary>Construct over the tax stores, jurisdiction store, and account resolver.</summary>
    public Pass2TaxUpserter(
        ITaxCodeStore codes,
        ITaxRateLookup rates,
        ITaxJurisdictionStore jurisdictions,
        IAccountResolver accounts,
        TimeProvider time)
    {
        _codes = codes ?? throw new ArgumentNullException(nameof(codes));
        _rates = rates ?? throw new ArgumentNullException(nameof(rates));
        _jurisdictions = jurisdictions ?? throw new ArgumentNullException(nameof(jurisdictions));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<ImportOutcome<TaxCode>> UpsertTaxTemplateAsync(
        ErpnextTaxTemplateSource source,
        FL.ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var at = new Instant(_time.GetUtcNow());

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            return Reject("(unnamed)", ImportRejectReason.MissingRequiredField, "name");
        }
        if (string.IsNullOrWhiteSpace(source.TaxName))
        {
            return Reject(source.Name, ImportRejectReason.MissingRequiredField, "tax_name");
        }

        var externalRefMarker = ExternalRefPrefix + source.Name;
        var existing = await FindByExternalRefAsync(targetChart, externalRefMarker, cancellationToken)
            .ConfigureAwait(false);

        // Idempotency: the dedupe marker carries the modified stamp.
        if (existing is not null && existing.Notes is not null
            && existing.Notes.Contains($"{ModifiedMarker}{source.Modified}", StringComparison.Ordinal))
        {
            return new ImportOutcome<TaxCode>.Skipped(existing, "ERPNext modified key unchanged.");
        }

        // Inclusive iff any child row is print-rate-inclusive (ERPNext compound
        // is a separate column we do not yet ingest).
        var application = source.Rates.Any(r => r.IncludedInPrintRate)
            ? TaxApplication.Inclusive
            : TaxApplication.OnSubtotal;
        var kind = application == TaxApplication.Inclusive ? TaxKind.VAT : TaxKind.Sales;
        var notes = $"{ExternalRefPrefix}{source.Name}{ModifiedMarker}{source.Modified}";

        TaxCode upserted;
        bool isInsert;
        if (existing is null)
        {
            upserted = TaxCode.Create(
                chartId: targetChart,
                code: source.TaxName,
                name: source.TaxName,
                kind: kind,
                application: application,
                createdAtUtc: at,
                notes: notes);
            if (source.Disabled) upserted = upserted with { IsActive = false };
            await _codes.UpsertAsync(upserted, cancellationToken).ConfigureAwait(false);
            isInsert = true;
        }
        else
        {
            upserted = existing with
            {
                Code = source.TaxName,
                Name = source.TaxName,
                Kind = kind,
                Application = application,
                IsActive = !source.Disabled,
                Notes = notes,
                UpdatedAtUtc = at,
            };
            await _codes.UpsertAsync(upserted, cancellationToken).ConfigureAwait(false);
            isInsert = false;
        }

        // Synthesize the category jurisdiction (best-effort; user reviews
        // post-import). Categoryless templates get a placeholder. Per
        // importer-spec §4.2 sub-pass 2 each rate row is its OWN TaxRate. An
        // ERPNext multi-row `taxes` table is a stack of charge components on
        // the SAME scope (charge_type OnSubtotal/Compound/Inclusive), NOT a
        // jurisdictional breakdown — ERPNext does not model jurisdictions. So
        // every row beyond the first gets a distinct SYNTHETIC placeholder at
        // the SAME level as row 0 (a flat, honestly-labelled "reconcile later"
        // node — matching the legacy ErpnextTaxImporter precedent). This keeps
        // the (TaxCode, Jurisdiction) same-effective-date overlap detector from
        // rejecting a legitimate multi-row import without inventing County/level
        // hierarchy the source lacks.
        var categoryJurisdiction = await SynthesizeJurisdictionAsync(source.TaxCategory, at, cancellationToken)
            .ConfigureAwait(false);

        // Pre-load existing rates for this TaxCode so the fan-out is idempotent
        // on re-import (Updated path re-runs the loop): a rate with the same
        // (TaxCodeId, JurisdictionId, EffectiveDate) is left in place rather
        // than appended (ADR 0100 idempotency — re-run never duplicate-inserts
        // child rows). Tax records carry no PII; only structural keys read here.
        var existingRates = await _rates.GetAllForTaxCodeAsync(upserted.Id, cancellationToken)
            .ConfigureAwait(false);

        var rowIndex = 0;
        var zeroRateDrops = 0;     // ERPNext informational 0-rate rows (counted, not logged).
        var unresolvedAccountDrops = 0; // account_head Pass-1 didn't seed (counted, not logged).
        var validationDrops = 0;   // ITaxRateLookup.UpsertAsync structured rejects (counted, not logged).
        var ratesWritten = 0;
        var ratesUnchanged = 0;
        foreach (var row in source.Rates)
        {
            if (row.Rate <= 0m)
            {
                zeroRateDrops++; // ERPNext often carries 0-rate informational rows.
                continue;
            }
            var account = await _accounts.GetAsync(new FL.GLAccountId(row.AccountHead), cancellationToken)
                .ConfigureAwait(false);
            if (account is null)
            {
                // Pass 1 should have seeded it; count the drop so re-import
                // diffing + the reconcile report can surface it (no PII logged).
                unresolvedAccountDrops++;
                continue;
            }

            var rowJurisdiction = await ResolveRowJurisdictionAsync(
                categoryJurisdiction, rowIndex, at, cancellationToken).ConfigureAwait(false);
            rowIndex++;

            // Idempotency: a rate already present for this
            // (TaxCodeId, JurisdictionId, EffectiveDate) needs no write.
            var alreadyPresent = existingRates.Any(r =>
                r.JurisdictionId == rowJurisdiction.Id
                && r.EffectiveDate == HistoryEffectiveDate);
            if (alreadyPresent)
            {
                ratesUnchanged++;
                continue;
            }

            var candidate = TaxRate.Create(
                taxCodeId: upserted.Id,
                jurisdictionId: rowJurisdiction.Id,
                ratePercent: row.Rate,
                effectiveDate: HistoryEffectiveDate,
                payableAccountId: account.Id,
                createdAtUtc: at);
            var result = await _rates.UpsertAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (result.Error != TaxRateValidationError.None || result.Rate is null)
            {
                // A structured (non-throwing) validation reject — wrong payable
                // subtype, date-range overlap, etc. The TaxCode still imports,
                // but the dropped rate MUST leave a count for the reconcile
                // report (C2/C5 silent-drop guard). Count only — never log the
                // record contents / account values (real-financial-domain).
                validationDrops++;
                continue;
            }
            ratesWritten++;
        }

        var detail = BuildDetail(
            source.Rates.Count, ratesWritten, ratesUnchanged,
            zeroRateDrops, unresolvedAccountDrops, validationDrops);
        return isInsert
            ? new ImportOutcome<TaxCode>.Inserted(upserted, detail)
            : new ImportOutcome<TaxCode>.Updated(upserted, detail);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<TaxCode?> FindByExternalRefAsync(
        FL.ChartOfAccountsId chartId,
        string externalRefMarker,
        CancellationToken cancellationToken)
    {
        var all = await _codes.GetByChartAsync(chartId, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(c => c.Notes is not null
            && c.Notes.StartsWith(externalRefMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reuse an existing jurisdiction synthesized for the same category, or mint
    /// a fresh one. A null/blank category mints a per-template placeholder.
    /// </summary>
    private async Task<TaxJurisdiction> SynthesizeJurisdictionAsync(
        string? taxCategory,
        Instant at,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(taxCategory))
        {
            var existing = await _jurisdictions.GetByLevelAsync(JurisdictionLevel.State, cancellationToken)
                .ConfigureAwait(false);
            var match = existing.FirstOrDefault(j =>
                string.Equals(j.Name, taxCategory!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            var synthesized = TaxJurisdiction.Create(
                level: JurisdictionLevel.State,
                isoCountry: "US",
                name: taxCategory!.Trim(),
                createdAtUtc: at,
                notes: "Synthesized from ERPNext Tax Category (review post-import).");
            await _jurisdictions.UpsertAsync(synthesized, cancellationToken).ConfigureAwait(false);
            return synthesized;
        }

        var placeholder = TaxJurisdiction.Create(
            level: JurisdictionLevel.State,
            isoCountry: "US",
            name: "(uncategorized ERPNext tax)",
            createdAtUtc: at,
            notes: "Placeholder jurisdiction for a categoryless ERPNext template (review post-import).");
        await _jurisdictions.UpsertAsync(placeholder, cancellationToken).ConfigureAwait(false);
        return placeholder;
    }

    /// <summary>
    /// Row 0 uses the category jurisdiction directly (the common single-row
    /// case). Each subsequent row of an ERPNext multi-row <c>taxes</c> stack
    /// gets a distinct FLAT placeholder at the SAME level as row 0 — ERPNext
    /// does not model jurisdictions, so these are synthetic "reconcile later"
    /// nodes, NOT a fabricated County hierarchy. Reused on re-import by stable
    /// name lookup at the category level (mirrors the category-synthesis path),
    /// so the fan-out is idempotent and the same-effective-date overlap
    /// detector never rejects a legitimate multi-row template.
    /// </summary>
    private async Task<TaxJurisdiction> ResolveRowJurisdictionAsync(
        TaxJurisdiction categoryJurisdiction,
        int rowIndex,
        Instant at,
        CancellationToken cancellationToken)
    {
        if (rowIndex == 0) return categoryJurisdiction;

        // Stable synthetic name for re-use across re-imports (component N at the
        // same level as the category node — flat, not a hierarchy).
        var componentName = $"{categoryJurisdiction.Name} (component {rowIndex + 1})";
        var siblings = await _jurisdictions.GetByLevelAsync(categoryJurisdiction.Level, cancellationToken)
            .ConfigureAwait(false);
        var match = siblings.FirstOrDefault(j =>
            string.Equals(j.Name, componentName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        var component = TaxJurisdiction.Create(
            level: categoryJurisdiction.Level,
            isoCountry: categoryJurisdiction.IsoCountry,
            name: componentName,
            createdAtUtc: at,
            notes: "Synthesized placeholder for a component row of an ERPNext multi-row tax template (review post-import).");
        await _jurisdictions.UpsertAsync(component, cancellationToken).ConfigureAwait(false);
        return component;
    }

    /// <summary>
    /// Compose the audit/reconcile detail string. Reports rate rows written,
    /// rows left unchanged on a re-import (idempotency), and each dropped-row
    /// category by COUNT only — never the rate value, account, or any record
    /// content (real-financial-domain: no PII / financial-record contents
    /// logged). The drop counts let A6 reconcile + re-import diffing surface
    /// invisible rate-level drops that the TaxCode-granularity census misses.
    /// </summary>
    private static string BuildDetail(
        int sourceRowCount,
        int ratesWritten,
        int ratesUnchanged,
        int zeroRateDrops,
        int unresolvedAccountDrops,
        int validationDrops)
    {
        var detail = $"Imported with {sourceRowCount} rate row(s): "
            + $"{ratesWritten} written, {ratesUnchanged} unchanged.";
        var drops = zeroRateDrops + unresolvedAccountDrops + validationDrops;
        if (drops > 0)
        {
            detail += $" Dropped {drops} rate row(s) "
                + $"(zero-rate: {zeroRateDrops}, unresolved-account: {unresolvedAccountDrops}, "
                + $"validation-reject: {validationDrops}) — review reconcile report.";
        }
        return detail;
    }

    private static ImportOutcome<TaxCode> Reject(
        string externalRef,
        ImportRejectReason reason,
        string? fieldName = null,
        string? ruleViolated = null)
        => new ImportOutcome<TaxCode>.Rejected(
            ImportFailure.Of(externalRef, TaxDocType, reason, fieldName, ruleViolated));
}
