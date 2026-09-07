// ADR 0120 PR-E2: AgingBucket consolidated to blocks-financial-ledger (the shared LOW tier
// that AR, AP, and subledger all already depend on). Removed the duplicate definition that
// used to live here; callers in this assembly pick it up via the global using alias below.
// blocks-financial-subledger depends on blocks-financial-ledger → no new project reference.
global using AgingBucket = Harborline.Api.Blocks.FinancialLedger.Models.AgingBucket;

using Harborline.Api.Blocks.FinancialSubLedger.Services;

namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>
/// One row of a sub-ledger aging snapshot. Scoped to one
/// <see cref="SubLedgerAccount"/> (a finer scope than the AR/AP cluster's
/// chart/customer/property scopes). Returned as part of
/// <see cref="AgingSummary.Rows"/>.
/// </summary>
/// <param name="SourceId">Opaque id of the source invoice or bill.</param>
/// <param name="SourceNumber">Human-readable document number for display.</param>
/// <param name="IssueDate">Issue date for secondary sort.</param>
/// <param name="DueDate">Due date — the bucket anchor.</param>
/// <param name="DaysPastDue">Negative when not yet due; floor-zero for Current.</param>
/// <param name="Total">Original total of the open item.</param>
/// <param name="AmountPaid">Cumulative payment applied.</param>
/// <param name="Balance">Open balance — rolls up into the bucket totals.</param>
/// <param name="Bucket">The aging bucket for this row.</param>
public sealed record AgingRow(
    string SourceId,
    string SourceNumber,
    DateOnly IssueDate,
    DateOnly DueDate,
    int DaysPastDue,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    AgingBucket Bucket);

/// <summary>
/// Aggregate aging totals for one <see cref="SubLedgerAccount"/> at a
/// snapshot date. Returned by
/// <see cref="ISubLedgerReadModel.GetAgingForSubLedgerAsync"/>.
/// </summary>
/// <param name="SubLedgerAccountId">The account this snapshot belongs to.</param>
/// <param name="AsOf">Snapshot date used to compute days-past-due.</param>
/// <param name="Current">Sum of open-item <c>Balance</c> in <see cref="AgingBucket.Current"/>.</param>
/// <param name="Days0To30">Sum in 1–30 days past due.</param>
/// <param name="Days31To60">Sum in 31–60 days past due.</param>
/// <param name="Days61To90">Sum in 61–90 days past due.</param>
/// <param name="Days90Plus">Sum in 91+ days past due.</param>
/// <param name="Total">Convenience: sum of the five bucket totals.</param>
/// <param name="Rows">Contributing rows in deterministic order (DueDate ascending, then SourceNumber).</param>
public sealed record AgingSummary(
    SubLedgerAccountId SubLedgerAccountId,
    DateOnly AsOf,
    decimal Current,
    decimal Days0To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Days90Plus,
    decimal Total,
    IReadOnlyList<AgingRow> Rows)
{
    /// <summary>Empty snapshot — useful as a default when no open items exist.</summary>
    public static AgingSummary Empty(SubLedgerAccountId id, DateOnly asOf)
        => new(id, asOf, 0m, 0m, 0m, 0m, 0m, 0m, Array.Empty<AgingRow>());
}
