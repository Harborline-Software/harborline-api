using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>
/// A single pay run: one posting of payroll amounts for one or more employees
/// over a defined period. In v1 manual-entry mode the operator supplies all
/// <see cref="PayRunLine"/> amounts; the
/// <see cref="Services.IPayRunPostingService"/> maps them to a balanced GL
/// journal entry.
/// </summary>
/// <remarks>
/// Invariants:
/// <list type="bullet">
///   <item><see cref="Lines"/> must be non-empty before posting.</item>
///   <item>Only <see cref="PayRunStatus.Draft"/> runs may be posted.</item>
///   <item>Only <see cref="PayRunStatus.Posted"/> runs may be reversed.</item>
/// </list>
/// </remarks>
public sealed record PayRun : IMustHaveTenant
{
    /// <summary>Unique pay run identifier.</summary>
    public PayRunId Id { get; }

    /// <summary>Tenant scope. Required per <see cref="IMustHaveTenant"/>.</summary>
    public TenantId TenantId { get; }

    /// <summary>Operator-supplied label for this run (e.g. "June 2026 bi-weekly").</summary>
    public string Label { get; }

    /// <summary>First day of the pay period this run covers.</summary>
    public DateOnly PeriodStart { get; }

    /// <summary>Last day of the pay period this run covers.</summary>
    public DateOnly PeriodEnd { get; }

    /// <summary>Date the journal entry is effective (usually same as <see cref="PeriodEnd"/>).</summary>
    public DateOnly PostingDate { get; }

    /// <summary>Lifecycle state of this pay run.</summary>
    public PayRunStatus Status { get; init; } = PayRunStatus.Draft;

    /// <summary>
    /// Per-employee amounts entered by the operator. Empty in Draft until
    /// the operator adds lines; must be non-empty before posting.
    /// </summary>
    public IReadOnlyList<PayRunLine> Lines { get; init; } = Array.Empty<PayRunLine>();

    // ── Default GL accounts (used when a PayRunLine does not override) ──────

    /// <summary>
    /// Default GL liability account for employee-withheld tax across all
    /// lines in this run.
    /// </summary>
    public GLAccountId DefaultTaxWithheldAccountId { get; }

    /// <summary>
    /// Default GL liability account for employee deductions across all
    /// lines in this run.
    /// </summary>
    public GLAccountId DefaultDeductionPayableAccountId { get; }

    /// <summary>
    /// Default GL expense account for employer-side contributions/taxes
    /// across all lines in this run.
    /// </summary>
    public GLAccountId DefaultEmployerLiabilityExpenseAccountId { get; }

    /// <summary>
    /// Default GL liability account for employer-side contributions payable
    /// across all lines in this run.
    /// </summary>
    public GLAccountId DefaultEmployerLiabilityPayableAccountId { get; }

    // ── Post-posting fields ─────────────────────────────────────────────────

    /// <summary>
    /// FK to the GL journal entry posted when this run was submitted.
    /// Null while <see cref="Status"/> is <see cref="PayRunStatus.Draft"/>.
    /// </summary>
    public JournalEntryId? JournalEntryId { get; init; }

    /// <summary>
    /// FK to the reversal journal entry posted when this run was reversed.
    /// Null unless <see cref="Status"/> is <see cref="PayRunStatus.Reversed"/>.
    /// </summary>
    public JournalEntryId? ReversalEntryId { get; init; }

    /// <summary>Wall-clock instant at which this run was created.</summary>
    public Instant CreatedAtUtc { get; }

    /// <summary>Wall-clock instant of the last status change.</summary>
    public Instant? UpdatedAtUtc { get; init; }

    /// <summary>Optimistic concurrency version stamp.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Constructs a new pay run record.</summary>
    public PayRun(
        PayRunId id,
        TenantId tenantId,
        string label,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly postingDate,
        GLAccountId defaultTaxWithheldAccountId,
        GLAccountId defaultDeductionPayableAccountId,
        GLAccountId defaultEmployerLiabilityExpenseAccountId,
        GLAccountId defaultEmployerLiabilityPayableAccountId,
        Instant createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Pay run label is required.", nameof(label));
        if (periodEnd < periodStart)
            throw new ArgumentException(
                $"PeriodEnd ({periodEnd}) must be on or after PeriodStart ({periodStart}).",
                nameof(periodEnd));
        if (postingDate < periodStart)
            throw new ArgumentException(
                $"PostingDate ({postingDate}) must be on or after PeriodStart ({periodStart}).",
                nameof(postingDate));

        Id = id;
        TenantId = tenantId;
        Label = label;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        PostingDate = postingDate;
        DefaultTaxWithheldAccountId = defaultTaxWithheldAccountId;
        DefaultDeductionPayableAccountId = defaultDeductionPayableAccountId;
        DefaultEmployerLiabilityExpenseAccountId = defaultEmployerLiabilityExpenseAccountId;
        DefaultEmployerLiabilityPayableAccountId = defaultEmployerLiabilityPayableAccountId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Factory method for creating a new Draft pay run with a generated id
    /// and current timestamp.
    /// </summary>
    public static PayRun Create(
        TenantId tenantId,
        string label,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly postingDate,
        GLAccountId defaultTaxWithheldAccountId,
        GLAccountId defaultDeductionPayableAccountId,
        GLAccountId defaultEmployerLiabilityExpenseAccountId,
        GLAccountId defaultEmployerLiabilityPayableAccountId,
        Instant createdAtUtc)
    {
        return new PayRun(
            id: PayRunId.NewId(),
            tenantId: tenantId,
            label: label,
            periodStart: periodStart,
            periodEnd: periodEnd,
            postingDate: postingDate,
            defaultTaxWithheldAccountId: defaultTaxWithheldAccountId,
            defaultDeductionPayableAccountId: defaultDeductionPayableAccountId,
            defaultEmployerLiabilityExpenseAccountId: defaultEmployerLiabilityExpenseAccountId,
            defaultEmployerLiabilityPayableAccountId: defaultEmployerLiabilityPayableAccountId,
            createdAtUtc: createdAtUtc);
    }
}
