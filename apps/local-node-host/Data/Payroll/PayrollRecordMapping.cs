using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// Record↔domain-model translation for the node-local payroll repos (T4 local-first sweep).
/// </summary>
/// <remarks>
/// The node persists flat <see cref="EmployeeRecord"/> / <see cref="PayRunRecord"/> /
/// <see cref="FilingObligationRecord"/> rows (the <c>LeaseRecord</c> Cohort-C precedent) and the Node
/// EF repos map them to and from the block domain models here, so <c>PayRunPostingService</c> reuses
/// the block <c>IEmployeeRepository</c>/<c>IPayRunRepository</c> contracts verbatim. The reverse
/// direction reconstructs the validated value-object <c>PayRunLine</c> ctor from the persisted line
/// columns (the run was already validated when first written, so the round-trip is benign).
/// </remarks>
internal static class PayrollRecordMapping
{
    // ── Employee ───────────────────────────────────────────────────────────

    public static Employee ToDomain(EmployeeRecord r) =>
        new Employee(
            id: new EmployeeId(r.Id),
            tenantId: new TenantId(r.TenantId),
            partyId: new PartyId(r.PartyId),
            displayName: r.DisplayName,
            wageExpenseAccountId: new GLAccountId(r.WageExpenseAccountId),
            wagesPayableAccountId: new GLAccountId(r.WagesPayableAccountId),
            createdAtUtc: new Instant(r.CreatedAtUtc))
        {
            CostCentreId = string.IsNullOrWhiteSpace(r.CostCentreId)
                ? (ClassificationId?)null
                : new ClassificationId(r.CostCentreId),
            IsActive = r.IsActive,
            UpdatedAtUtc = r.UpdatedAtUtc.HasValue ? new Instant(r.UpdatedAtUtc.Value) : (Instant?)null,
        };

    public static void CopyInto(Employee e, EmployeeRecord r)
    {
        r.Id = e.Id.Value;
        r.TenantId = e.TenantId.Value;
        r.PartyId = e.PartyId.Value;
        r.DisplayName = e.DisplayName;
        r.CostCentreId = e.CostCentreId?.Value;
        r.WageExpenseAccountId = e.WageExpenseAccountId.Value;
        r.WagesPayableAccountId = e.WagesPayableAccountId.Value;
        r.IsActive = e.IsActive;
        r.CreatedAtUtc = e.CreatedAtUtc.Value;
        r.UpdatedAtUtc = e.UpdatedAtUtc?.Value;
    }

    public static EmployeeRecord ToRecord(Employee e)
    {
        var r = new EmployeeRecord { Id = e.Id.Value };
        CopyInto(e, r);
        return r;
    }

    // ── PayRun ──────────────────────────────────────────────────────────────

    public static PayRun ToDomain(PayRunRecord r)
    {
        var run = new PayRun(
            id: new PayRunId(r.Id),
            tenantId: new TenantId(r.TenantId),
            label: r.Label,
            periodStart: DateOnly.Parse(r.PeriodStart, System.Globalization.CultureInfo.InvariantCulture),
            periodEnd: DateOnly.Parse(r.PeriodEnd, System.Globalization.CultureInfo.InvariantCulture),
            postingDate: DateOnly.Parse(r.PostingDate, System.Globalization.CultureInfo.InvariantCulture),
            defaultTaxWithheldAccountId: new GLAccountId(r.DefaultTaxWithheldAccountId),
            defaultDeductionPayableAccountId: new GLAccountId(r.DefaultDeductionPayableAccountId),
            defaultEmployerLiabilityExpenseAccountId: new GLAccountId(r.DefaultEmployerLiabilityExpenseAccountId),
            defaultEmployerLiabilityPayableAccountId: new GLAccountId(r.DefaultEmployerLiabilityPayableAccountId),
            createdAtUtc: new Instant(r.CreatedAtUtc));

        var lines = r.Lines
            .OrderBy(l => l.Ordinal)
            .Select(ToDomain)
            .ToList();

        return run with
        {
            Status = Enum.Parse<PayRunStatus>(r.Status, ignoreCase: true),
            Lines = lines,
            JournalEntryId = string.IsNullOrWhiteSpace(r.JournalEntryId)
                ? (JournalEntryId?)null : new JournalEntryId(r.JournalEntryId),
            ReversalEntryId = string.IsNullOrWhiteSpace(r.ReversalEntryId)
                ? (JournalEntryId?)null : new JournalEntryId(r.ReversalEntryId),
            UpdatedAtUtc = r.UpdatedAtUtc.HasValue ? new Instant(r.UpdatedAtUtc.Value) : (Instant?)null,
            Version = r.Version,
        };
    }

    private static PayRunLine ToDomain(PayRunLineRecord l) =>
        new PayRunLine(
            employeeId: new EmployeeId(l.EmployeeId),
            grossWage: l.GrossWage,
            taxWithheld: l.TaxWithheld,
            employeeDeductions: l.EmployeeDeductions,
            employerLiabilityAmount: l.EmployerLiabilityAmount,
            taxWithheldAccountId: new GLAccountId(l.TaxWithheldAccountId),
            deductionPayableAccountId: new GLAccountId(l.DeductionPayableAccountId),
            employerLiabilityExpenseAccountId: new GLAccountId(l.EmployerLiabilityExpenseAccountId),
            employerLiabilityPayableAccountId: new GLAccountId(l.EmployerLiabilityPayableAccountId))
        {
            Notes = l.Notes,
        };

    /// <summary>
    /// Copies the domain pay-run's scalar fields into the record and REPLACES the line collection
    /// (the repo clears existing line rows first so an upsert of a re-supplied line set is faithful).
    /// </summary>
    public static void CopyInto(PayRun run, PayRunRecord r)
    {
        r.Id = run.Id.Value;
        r.TenantId = run.TenantId.Value;
        r.Label = run.Label;
        r.PeriodStart = run.PeriodStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        r.PeriodEnd = run.PeriodEnd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        r.PostingDate = run.PostingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        r.Status = run.Status.ToString();
        r.DefaultTaxWithheldAccountId = run.DefaultTaxWithheldAccountId.Value;
        r.DefaultDeductionPayableAccountId = run.DefaultDeductionPayableAccountId.Value;
        r.DefaultEmployerLiabilityExpenseAccountId = run.DefaultEmployerLiabilityExpenseAccountId.Value;
        r.DefaultEmployerLiabilityPayableAccountId = run.DefaultEmployerLiabilityPayableAccountId.Value;
        r.JournalEntryId = run.JournalEntryId?.Value;
        r.ReversalEntryId = run.ReversalEntryId?.Value;
        r.CreatedAtUtc = run.CreatedAtUtc.Value;
        r.UpdatedAtUtc = run.UpdatedAtUtc?.Value;
        r.Version = run.Version;
    }

    public static List<PayRunLineRecord> ToLineRecords(PayRun run)
    {
        var lines = new List<PayRunLineRecord>(run.Lines.Count);
        for (var i = 0; i < run.Lines.Count; i++)
        {
            var l = run.Lines[i];
            lines.Add(new PayRunLineRecord
            {
                PayRunId = run.Id.Value,
                Ordinal = i,
                EmployeeId = l.EmployeeId.Value,
                GrossWage = l.GrossWage,
                TaxWithheld = l.TaxWithheld,
                EmployeeDeductions = l.EmployeeDeductions,
                EmployerLiabilityAmount = l.EmployerLiabilityAmount,
                TaxWithheldAccountId = l.TaxWithheldAccountId.Value,
                DeductionPayableAccountId = l.DeductionPayableAccountId.Value,
                EmployerLiabilityExpenseAccountId = l.EmployerLiabilityExpenseAccountId.Value,
                EmployerLiabilityPayableAccountId = l.EmployerLiabilityPayableAccountId.Value,
                Notes = l.Notes,
            });
        }
        return lines;
    }

    // ── FilingObligation ──────────────────────────────────────────────────────

    public static FilingObligation ToDomain(FilingObligationRecord r) =>
        new FilingObligation(
            id: new FilingObligationId(r.Id),
            tenantId: new TenantId(r.TenantId),
            label: r.Label,
            dueDate: DateOnly.Parse(r.DueDate, System.Globalization.CultureInfo.InvariantCulture),
            createdAtUtc: new Instant(r.CreatedAtUtc))
        {
            PeriodStart = string.IsNullOrWhiteSpace(r.PeriodStart) ? (DateOnly?)null : DateOnly.Parse(r.PeriodStart, System.Globalization.CultureInfo.InvariantCulture),
            PeriodEnd = string.IsNullOrWhiteSpace(r.PeriodEnd) ? (DateOnly?)null : DateOnly.Parse(r.PeriodEnd, System.Globalization.CultureInfo.InvariantCulture),
            JurisdictionCode = r.JurisdictionCode,
            SourcePayRunId = string.IsNullOrWhiteSpace(r.SourcePayRunId) ? (PayRunId?)null : new PayRunId(r.SourcePayRunId),
            IsComplete = r.IsComplete,
            Notes = r.Notes,
        };

    public static void CopyInto(FilingObligation o, FilingObligationRecord r)
    {
        r.Id = o.Id.Value;
        r.TenantId = o.TenantId.Value;
        r.Label = o.Label;
        r.DueDate = o.DueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        r.PeriodStart = o.PeriodStart?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        r.PeriodEnd = o.PeriodEnd?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        r.JurisdictionCode = o.JurisdictionCode;
        r.SourcePayRunId = o.SourcePayRunId?.Value;
        r.IsComplete = o.IsComplete;
        r.Notes = o.Notes;
        r.CreatedAtUtc = o.CreatedAtUtc.Value;
    }

    public static FilingObligationRecord ToRecord(FilingObligation o)
    {
        var r = new FilingObligationRecord { Id = o.Id.Value };
        CopyInto(o, r);
        return r;
    }
}
