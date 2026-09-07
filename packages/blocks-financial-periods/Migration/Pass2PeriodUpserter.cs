using System.Globalization;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialPeriods.Migration;

/// <summary>
/// Default <see cref="IPass2PeriodUpserter"/>. Reuses
/// <see cref="IFiscalYearRepository"/> + <see cref="IFiscalPeriodRepository"/> +
/// <see cref="FiscalPeriodFactory"/>; holds no state of its own and recreates no
/// domain logic. Returns the canonical A0 <see cref="ImportOutcome{T}"/> union.
/// </summary>
/// <remarks>
/// The <c>modified</c> stamp is parsed (not lex-compared) so a caller drift
/// fails loudly as a <see cref="ImportRejectReason.UnparseableSource"/> reject
/// rather than silently mis-ordering versions. A Closed local FY is NEVER
/// reopened — a later re-export updates label/dates but leaves
/// <see cref="FiscalYearStatus.Closed"/> intact (year-close is the
/// <c>FiscalYearCloseService</c> path, not the importer's).
/// </remarks>
public sealed class Pass2PeriodUpserter : IPass2PeriodUpserter
{
    private const string FiscalYearDocType = "Fiscal Year";

    /// <summary>
    /// ERPNext Frappe ORM emits <c>modified</c> as one of these fixed-width
    /// formats.
    /// </summary>
    private static readonly string[] ErpnextModifiedFormats =
    {
        "yyyy-MM-dd HH:mm:ss.ffffff",
        "yyyy-MM-dd HH:mm:ss",
    };

    private readonly IFiscalYearRepository _years;
    private readonly IFiscalPeriodRepository _periods;
    private readonly TimeProvider _time;

    /// <summary>Construct over the FY + period repositories and a clock.</summary>
    public Pass2PeriodUpserter(
        IFiscalYearRepository years,
        IFiscalPeriodRepository periods,
        TimeProvider time)
    {
        _years = years ?? throw new ArgumentNullException(nameof(years));
        _periods = periods ?? throw new ArgumentNullException(nameof(periods));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<ImportOutcome<FiscalYear>> UpsertFiscalYearAsync(
        ErpnextFiscalYearSource source,
        ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            return RejectYear("(unnamed)", ImportRejectReason.MissingRequiredField, "name");
        }

        if (!TryParseErpnextModified(source.Modified, out var sourceModified))
        {
            return RejectYear(source.Name, ImportRejectReason.UnparseableSource, "modified",
                "Frappe modified stamp does not match yyyy-MM-dd HH:mm:ss[.ffffff].");
        }

        var existing = await _years.GetByExternalRefAsync(source.Name, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var candidate = FiscalYear.CreateOpen(
                id: FiscalYearId.NewId(),
                chartId: targetChart,
                label: DeriveLabel(source),
                startDate: source.YearStartDate,
                endDate: source.YearEndDate,
                createdAtUtc: new Instant(_time.GetUtcNow()),
                externalRef: source.Name,
                externalModifiedAtUtc: sourceModified);

            var shapeErrors = candidate.Validate();
            if (shapeErrors.Count > 0)
            {
                // Surface only the offending field NAME — never echo the
                // validator message (it may interpolate the dates/label).
                return RejectYear(source.Name, ImportRejectReason.ConstraintViolation, "year_start_date");
            }

            await _years.InsertAsync(candidate, cancellationToken).ConfigureAwait(false);
            return new ImportOutcome<FiscalYear>.Inserted(candidate, "Fiscal year imported.");
        }

        // Decide Skipped vs Updated from the persisted version stamp
        // (process-restart safe — reads the row, not an in-process cache).
        var priorModified = existing.ExternalModifiedAtUtc?.Value ?? DateTimeOffset.MinValue;
        if (sourceModified.Value <= priorModified)
        {
            return new ImportOutcome<FiscalYear>.Skipped(existing, "ERPNext modified key unchanged or older.");
        }

        // Update path — refresh label + dates; never flip Status (Closed stays
        // Closed). ClosedAtUtc + ClosingJournalEntryId preserved by `with`.
        var updated = existing with
        {
            Label = DeriveLabel(source),
            StartDate = source.YearStartDate,
            EndDate = source.YearEndDate,
            Version = existing.Version + 1,
            ExternalModifiedAtUtc = sourceModified,
        };

        if (!await _years.UpdateAsync(updated, cancellationToken).ConfigureAwait(false))
        {
            // Lost a CAS race to a concurrent edit — treat as a no-write skip.
            return new ImportOutcome<FiscalYear>.Skipped(existing, "Update rejected by repository CAS (concurrent edit?).");
        }

        return new ImportOutcome<FiscalYear>.Updated(updated, "ERPNext modified key advanced.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportOutcome<FiscalPeriod>>> SynthesizePeriodsAsync(
        FiscalYearId fiscalYearId,
        FiscalPeriodKind kind = FiscalPeriodKind.Monthly,
        CancellationToken cancellationToken = default)
    {
        var fy = await _years.GetAsync(fiscalYearId, cancellationToken).ConfigureAwait(false);
        if (fy is null)
        {
            // Unknown FY — nothing to synthesize. Empty list; the orchestrator
            // records the FY itself (already counted in the year sub-pass).
            return Array.Empty<ImportOutcome<FiscalPeriod>>();
        }

        var existing = await _periods.GetByFiscalYearAsync(fy.Id, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            // Idempotent: periods already synthesized → Skipped for each.
            return existing
                .Select(p => (ImportOutcome<FiscalPeriod>)new ImportOutcome<FiscalPeriod>.Skipped(p, "Periods already synthesized."))
                .ToList();
        }

        var createdAt = new Instant(_time.GetUtcNow());
        var synthesized = kind switch
        {
            FiscalPeriodKind.Quarterly => FiscalPeriodFactory.BuildQuarterlyPeriods(fy, createdAt),
            FiscalPeriodKind.Annual => FiscalPeriodFactory.BuildAnnualPeriod(fy, createdAt),
            _ => FiscalPeriodFactory.BuildMonthlyPeriods(fy, createdAt),
        };

        // Defense-in-depth: the synthesized set must cover the FY contiguously.
        var validation = FiscalPeriodCollectionValidator.Validate(fy, synthesized);
        if (!validation.IsValid)
        {
            // Reject every synthesized period (none persisted). The reject's
            // ExternalRef is the FY's opaque external ref (or its id) — no PII;
            // periods carry no PII at all.
            var fyRef = fy.ExternalRef ?? fy.Id.ToString();
            return synthesized
                .Select(_ => (ImportOutcome<FiscalPeriod>)new ImportOutcome<FiscalPeriod>.Rejected(
                    ImportFailure.Of(fyRef, "Fiscal Period", ImportRejectReason.ConstraintViolation,
                        fieldName: "period_set",
                        ruleViolated: "Synthesized period set is not contiguous over the fiscal year.")))
                .ToList();
        }

        var outcomes = new List<ImportOutcome<FiscalPeriod>>(synthesized.Count);
        foreach (var p in synthesized)
        {
            await _periods.InsertAsync(p, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new ImportOutcome<FiscalPeriod>.Inserted(p, "Period synthesized."));
        }
        return outcomes;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool TryParseErpnextModified(string? modified, out Instant instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(modified)) return false;
        if (!DateTimeOffset.TryParseExact(
                modified,
                ErpnextModifiedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return false;
        }
        instant = new Instant(dto);
        return true;
    }

    private static string DeriveLabel(ErpnextFiscalYearSource source)
    {
        var shortName = string.IsNullOrWhiteSpace(source.CompanyShortName)
            ? null
            : source.CompanyShortName.Trim();
        var yearSuffix = source.YearStartDate.Year % 100;
        var fyToken = $"FY{yearSuffix:00}";
        return shortName is null ? fyToken : $"{shortName} {fyToken}";
    }

    private static ImportOutcome<FiscalYear> RejectYear(
        string externalRef,
        ImportRejectReason reason,
        string? fieldName = null,
        string? ruleViolated = null)
        => new ImportOutcome<FiscalYear>.Rejected(
            ImportFailure.Of(externalRef, FiscalYearDocType, reason, fieldName, ruleViolated));
}
