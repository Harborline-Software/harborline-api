using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using AuthorizationWriteContext = Harborline.Api.Foundation.Authorization.AuthorizationWriteContext;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// Default <see cref="IPayRunPostingService"/>. Maps manual-entry pay run
/// lines to balanced GL journal entries and persists them via
/// <see cref="IJournalPostingService"/>.
/// </summary>
/// <remarks>
/// GL mapping per employee line:
/// <list type="bullet">
///   <item>Debit WageExpenseAccountId    for GrossWage</item>
///   <item>Credit WagesPayableAccountId  for NetWage</item>
///   <item>Credit TaxWithheldAccountId   for TaxWithheld (if > 0)</item>
///   <item>Credit DeductionPayableAccountId for EmployeeDeductions (if > 0)</item>
///   <item>Debit  EmployerLiabilityExpenseAccountId for EmployerLiabilityAmount (if > 0)</item>
///   <item>Credit EmployerLiabilityPayableAccountId for EmployerLiabilityAmount (if > 0)</item>
/// </list>
/// Total debits  = sum(GrossWage) + sum(EmployerLiabilityAmount)
/// Total credits = sum(NetWage) + sum(TaxWithheld) + sum(EmployeeDeductions)
///               + sum(EmployerLiabilityAmount)
/// These are equal because NetWage = GrossWage - TaxWithheld - EmployeeDeductions.
/// </remarks>
public sealed class PayRunPostingService : IPayRunPostingService
{
    private readonly IPayRunRepository _payRuns;
    private readonly IEmployeeRepository _employees;
    private readonly IJournalPostingService _journals;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;

    public PayRunPostingService(
        ITenantContext tenantContext,
        IPayRunRepository payRuns,
        IEmployeeRepository employees,
        IJournalPostingService journals,
        TimeProvider? timeProvider = null)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _payRuns = payRuns ?? throw new ArgumentNullException(nameof(payRuns));
        _employees = employees ?? throw new ArgumentNullException(nameof(employees));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    private TenantId CurrentTenantId =>
        _tenantContext.Tenant?.Id
            ?? throw new InvalidOperationException(
                "PayRunPostingService requires a resolved tenant on the ambient ITenantContext.");

    // ──────────────────────────────────────────────────────────────────
    //  PostAsync
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<PostPayRunResult> PostAsync(
        PayRunId payRunId,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId;
        RequireTenant(authority, tenantId);
        var now = new Instant(authority.At);

        var run = await _payRuns.GetAsync(tenantId, payRunId, cancellationToken).ConfigureAwait(false);
        if (run is null)
            return new PostPayRunResult(null, null, PostPayRunError.UnknownPayRun,
                $"PayRun '{payRunId.Value}' does not exist.");

        // Idempotent: already-Posted run returns existing JE id.
        if (run.Status == PayRunStatus.Posted && run.JournalEntryId is not null)
            return new PostPayRunResult(run, run.JournalEntryId, PostPayRunError.None, "Already posted; no-op.");

        if (run.Status != PayRunStatus.Draft)
            return new PostPayRunResult(run, null, PostPayRunError.InvalidStatus,
                $"Cannot post a pay run in status '{run.Status}'. Only Draft runs can be posted.");

        if (run.Lines.Count == 0)
            return new PostPayRunResult(run, null, PostPayRunError.NoLines,
                "Pay run has no lines. Add at least one employee line before posting.");

        // Resolve all referenced employees in one pass.
        var employeeMap = new Dictionary<EmployeeId, Employee>(run.Lines.Count);
        foreach (var line in run.Lines)
        {
            if (employeeMap.ContainsKey(line.EmployeeId)) continue;
            var emp = await _employees.GetAsync(tenantId, line.EmployeeId, cancellationToken).ConfigureAwait(false);
            if (emp is null)
                return new PostPayRunResult(run, null, PostPayRunError.UnknownEmployee,
                    $"Employee '{line.EmployeeId.Value}' does not exist in tenant '{tenantId.Value}'.");
            employeeMap[line.EmployeeId] = emp;
        }

        // Build the journal entry lines from the pay run lines.
        var jeLines = BuildJournalEntryLines(run.Lines, employeeMap);

        var entry = new JournalEntry(
            id: JournalEntryId.NewId(),
            tenantId: tenantId,
            entryDate: run.PostingDate,
            memo: $"Payroll: {run.Label} ({run.PeriodStart:yyyy-MM-dd} to {run.PeriodEnd:yyyy-MM-dd})",
            lines: jeLines,
            createdAtUtc: now,
            sourceReference: $"payroll:{run.Id.Value}")
        {
            SourceKind = JournalEntrySource.Manual,
        };

        var postResult = await _journals.PostAsync(
            entry,
            authority,
            cancellationToken).ConfigureAwait(false);
        if (!postResult.IsSuccess)
            return new PostPayRunResult(run, null, PostPayRunError.JournalRejected, postResult.Detail);

        var posted = run with
        {
            Status = PayRunStatus.Posted,
            JournalEntryId = entry.Id,
            UpdatedAtUtc = now,
            Version = run.Version + 1,
        };
        await _payRuns.UpsertAsync(tenantId, posted, cancellationToken).ConfigureAwait(false);

        return new PostPayRunResult(posted, entry.Id, PostPayRunError.None, null);
    }

    // ──────────────────────────────────────────────────────────────────
    //  ReverseAsync
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ReversePayRunResult> ReverseAsync(
        PayRunId payRunId,
        string reason,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId;
        RequireTenant(authority, tenantId);
        var now = new Instant(authority.At);

        var run = await _payRuns.GetAsync(tenantId, payRunId, cancellationToken).ConfigureAwait(false);
        if (run is null)
            return new ReversePayRunResult(null, null, ReversePayRunError.UnknownPayRun,
                $"PayRun '{payRunId.Value}' does not exist.");

        if (run.Status != PayRunStatus.Posted)
            return new ReversePayRunResult(run, null, ReversePayRunError.InvalidStatus,
                $"Cannot reverse a pay run in status '{run.Status}'. Only Posted runs can be reversed.");

        if (run.JournalEntryId is null)
            return new ReversePayRunResult(run, null, ReversePayRunError.NoJournalEntry,
                "Pay run is Posted but has no journal entry reference (corrupted state).");

        // Resolve employees to rebuild the same lines (debits/credits swapped).
        var employeeMap = new Dictionary<EmployeeId, Employee>(run.Lines.Count);
        foreach (var line in run.Lines)
        {
            if (employeeMap.ContainsKey(line.EmployeeId)) continue;
            var emp = await _employees.GetAsync(tenantId, line.EmployeeId, cancellationToken).ConfigureAwait(false);
            if (emp is not null)
                employeeMap[line.EmployeeId] = emp;
        }

        // Build reversal: same GL lines with debit and credit swapped.
        var originalLines = BuildJournalEntryLines(run.Lines, employeeMap);
        var reversalLines = originalLines
            .Select(l => new JournalEntryLine(l.AccountId, debit: l.Credit, credit: l.Debit, l.Notes)
            {
                ClassId = l.ClassId,
            })
            .ToList();

        var reversal = new JournalEntry(
            id: JournalEntryId.NewId(),
            tenantId: tenantId,
            entryDate: DateOnly.FromDateTime(authority.At.UtcDateTime),
            memo: $"Reverse payroll: {run.Label} — {reason}",
            lines: reversalLines,
            createdAtUtc: now,
            sourceReference: $"payroll-reversal:{run.Id.Value}")
        {
            SourceKind = JournalEntrySource.Reversal,
            ReversalOf = run.JournalEntryId,
        };

        var postResult = await _journals.PostAsync(
            reversal,
            authority,
            cancellationToken).ConfigureAwait(false);
        if (!postResult.IsSuccess)
            return new ReversePayRunResult(run, null, ReversePayRunError.JournalRejected, postResult.Detail);

        var reversed = run with
        {
            Status = PayRunStatus.Reversed,
            ReversalEntryId = reversal.Id,
            UpdatedAtUtc = now,
            Version = run.Version + 1,
        };
        await _payRuns.UpsertAsync(tenantId, reversed, cancellationToken).ConfigureAwait(false);

        return new ReversePayRunResult(reversed, reversal.Id, ReversePayRunError.None, null);
    }

    private static void RequireTenant(AuthorizationWriteContext authority, TenantId tenant)
    {
        if (authority.Tenant != tenant)
            throw new ArgumentException("The pay-run tenant does not match the boundary authority.", nameof(authority));
    }

    // ──────────────────────────────────────────────────────────────────
    //  GL mapping
    // ──────────────────────────────────────────────────────────────────

    private static List<JournalEntryLine> BuildJournalEntryLines(
        IReadOnlyList<PayRunLine> runLines,
        IReadOnlyDictionary<EmployeeId, Employee> employeeMap)
    {
        var jeLines = new List<JournalEntryLine>(runLines.Count * 4);

        foreach (var line in runLines)
        {
            employeeMap.TryGetValue(line.EmployeeId, out var emp);
            var classId = emp?.CostCentreId;
            var empNote = emp?.DisplayName;

            // Debit: Wage Expense (gross)
            if (emp is not null)
            {
                jeLines.Add(new JournalEntryLine(
                    emp.WageExpenseAccountId,
                    debit: line.GrossWage,
                    credit: 0m,
                    notes: empNote)
                {
                    ClassId = classId,
                });

                // Credit: Wages Payable (net take-home)
                jeLines.Add(new JournalEntryLine(
                    emp.WagesPayableAccountId,
                    debit: 0m,
                    credit: line.NetWage,
                    notes: empNote)
                {
                    ClassId = classId,
                });
            }

            // Credit: Tax Withheld (employee PAYE — liability until remitted)
            if (line.TaxWithheld > 0m)
            {
                jeLines.Add(new JournalEntryLine(
                    line.TaxWithheldAccountId,
                    debit: 0m,
                    credit: line.TaxWithheld,
                    notes: empNote)
                {
                    ClassId = classId,
                });
            }

            // Credit: Deductions Payable (benefits, garnishments — liability until remitted)
            if (line.EmployeeDeductions > 0m)
            {
                jeLines.Add(new JournalEntryLine(
                    line.DeductionPayableAccountId,
                    debit: 0m,
                    credit: line.EmployeeDeductions,
                    notes: empNote)
                {
                    ClassId = classId,
                });
            }

            // Employer-side contributions / payroll tax (additional cost, not from gross)
            if (line.EmployerLiabilityAmount > 0m)
            {
                // Debit: Employer Liability Expense
                jeLines.Add(new JournalEntryLine(
                    line.EmployerLiabilityExpenseAccountId,
                    debit: line.EmployerLiabilityAmount,
                    credit: 0m,
                    notes: empNote)
                {
                    ClassId = classId,
                });

                // Credit: Employer Liability Payable
                jeLines.Add(new JournalEntryLine(
                    line.EmployerLiabilityPayableAccountId,
                    debit: 0m,
                    credit: line.EmployerLiabilityAmount,
                    notes: empNote)
                {
                    ClassId = classId,
                });
            }
        }

        return jeLines;
    }
}
