using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialSubLedger.Projection.Services;

/// <summary>
/// Output of <see cref="SubLedgerMigrationService.MigrateAsync"/> (ADR 0120 PR-C).
/// Surfaces data-quality findings rather than silently absorbing mismatches.
/// </summary>
public sealed class SubLedgerMigrationReport
{
    public TenantId TenantId { get; }
    public ChartOfAccountsId ChartId { get; }

    // ── Mint counters ────────────────────────────────────────────────────────
    public int AccountsMinted  { get; internal set; }
    public int AccountsSkipped { get; internal set; }   // already existed (idempotent)

    // ── Backfill counters ────────────────────────────────────────────────────
    public int InvoicesBackfilled    { get; internal set; }
    public int InvoicesAlreadyStamped { get; internal set; }
    public int BillsBackfilled       { get; internal set; }
    public int BillsAlreadyStamped   { get; internal set; }

    /// <summary>Invoice ids for which no matching sub-ledger account could be resolved.</summary>
    public List<string> InvoicesUnresolved { get; } = new();

    /// <summary>Bill ids for which no matching sub-ledger account could be resolved.</summary>
    public List<string> BillsUnresolved { get; } = new();

    // ── R1 reconciliation findings ────────────────────────────────────────────
    /// <summary>
    /// Accounts whose post-backfill computed balance is anomalous.
    /// A non-empty list indicates data-quality issues (over-application, mismatched
    /// credit direction, etc.) that require manual review. Never silently absorbed.
    /// </summary>
    public List<SubLedgerReconciliationFinding> ReconciliationFindings { get; } = new();

    /// <summary>
    /// R1 cross-foot findings (ADR 0120 PR-D).
    /// Each entry surfaces a control account whose <c>Σ(sub-ledger positions)</c> does NOT
    /// equal the GL control balance — indicating un-stamped open items that were not caught
    /// by the backfill (or an over-application). Populated only when
    /// <c>SubLedgerMigrationService</c> was constructed with an <c>IGeneralLedgerReadModel</c>.
    /// </summary>
    public List<SubLedgerR1CrossFootFinding> R1CrossFootFindings { get; } = new();

    /// <summary>
    /// True when the migration completed with no unresolved items, no per-account anomalies,
    /// and no R1 cross-foot mismatches.
    /// </summary>
    public bool IsClean =>
        InvoicesUnresolved.Count == 0
        && BillsUnresolved.Count == 0
        && ReconciliationFindings.Count == 0
        && R1CrossFootFindings.Count == 0;

    public SubLedgerMigrationReport(TenantId tenantId, ChartOfAccountsId chartId)
    {
        TenantId = tenantId;
        ChartId  = chartId;
    }
}

/// <summary>
/// A per-account data-quality finding surfaced during the post-backfill R1 reconciliation pass.
/// </summary>
public sealed record SubLedgerReconciliationFinding(
    string SubLedgerAccountId,
    string PartyId,
    string ControlAccountId,
    SubLedgerKind Kind,
    decimal ComputedBalance,
    string Finding);

/// <summary>
/// An R1 cross-foot finding: a control account where
/// <c>Σ(sub-ledger positions)</c> does NOT equal the GL balance (ADR 0120 PR-D).
/// Indicates un-stamped open items or over-application not captured by the
/// per-account negative-balance check.
/// </summary>
public sealed record SubLedgerR1CrossFootFinding(
    string ControlAccountId,
    SubLedgerKind Kind,
    decimal SubLedgerSum,
    decimal GlBalance,
    decimal Delta,
    string Finding);
