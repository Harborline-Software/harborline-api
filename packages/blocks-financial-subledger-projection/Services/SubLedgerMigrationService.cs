using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialSubLedger.Projection.Services;

/// <summary>
/// ADR 0120 PR-C migration backfill — Step 3 of the implementation sequence.
///
/// <para>
/// <b>Responsibility:</b>
/// <list type="number">
///   <item>Mint <see cref="SubLedgerAccount"/>s by <b>derivation</b> on
///     <c>(ChartId, ControlAccountId, PartyId)</c> (idempotent/re-runnable) — one account
///     per distinct tuple. For legacy data without lease-grain context, per-party derivation
///     is the correct best-effort (the PM pack's per-lease FK-setting on rent invoices
///     lands in PR-D).</item>
///   <item>Backfill <see cref="Invoice.SubLedgerAccountId"/> and
///     <see cref="Bill.SubLedgerAccountId"/> on existing open items by matching to the
///     minted account.</item>
///   <item>R1-reconcile post-migration and <b>surface mismatches as data-quality findings</b>
///     (never silently absorb). Returns a <see cref="SubLedgerMigrationReport"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Idempotency:</b> re-running produces the same outcome — accounts are minted via
/// <see cref="ISubLedgerAccountRepository.GetByExternalRefAsync"/> with a stable
/// derivation key as <c>ExternalRef</c>; invoices/bills already stamped are skipped.
/// </para>
///
/// <para>
/// <b>Tenant isolation:</b> the caller supplies <c>tenantId</c> explicitly; all repository
/// calls pass it through. The service makes no assumption about ambient tenant context
/// (per ADR 0092 posture).
/// </para>
///
/// <para>
/// <b>No payment data is mutated.</b> Applications are untouched; the payment tie is
/// transitive through the open item (ADR 0120 D4).
/// </para>
/// </summary>
public sealed class SubLedgerMigrationService
{
    private readonly ISubLedgerAccountRepository _accounts;
    private readonly IInvoiceRepository _invoices;
    private readonly IBillRepository _bills;
    private readonly ISubLedgerReadModel _readModel;
    private readonly IGeneralLedgerReadModel? _glReadModel;
    private readonly TimeProvider _time;

    /// <summary>
    /// Construct without GL cross-foot capability (backwards-compatible — pre-PR-D callers).
    /// The per-account negative-balance R1 check still runs; the cross-foot is skipped.
    /// </summary>
    public SubLedgerMigrationService(
        ISubLedgerAccountRepository accounts,
        IInvoiceRepository invoices,
        IBillRepository bills,
        ISubLedgerReadModel readModel,
        TimeProvider time)
        : this(accounts, invoices, bills, readModel, time, glReadModel: null)
    { }

    /// <summary>
    /// Construct with GL cross-foot capability (ADR 0120 PR-D). When
    /// <paramref name="glReadModel"/> is non-null, <see cref="ReconcileAsync"/> also
    /// compares <c>Σ(sub-ledger positions)</c> to the GL control balance per
    /// control account, surfacing mismatches as <see cref="SubLedgerR1CrossFootFinding"/>s.
    /// </summary>
    public SubLedgerMigrationService(
        ISubLedgerAccountRepository accounts,
        IInvoiceRepository invoices,
        IBillRepository bills,
        ISubLedgerReadModel readModel,
        TimeProvider time,
        IGeneralLedgerReadModel? glReadModel)
    {
        _accounts    = accounts    ?? throw new ArgumentNullException(nameof(accounts));
        _invoices    = invoices    ?? throw new ArgumentNullException(nameof(invoices));
        _bills       = bills       ?? throw new ArgumentNullException(nameof(bills));
        _readModel   = readModel   ?? throw new ArgumentNullException(nameof(readModel));
        _time        = time        ?? throw new ArgumentNullException(nameof(time));
        _glReadModel = glReadModel; // optional — cross-foot only when non-null
    }

    /// <summary>
    /// Run the full migration backfill for a single tenant+chart combination.
    /// </summary>
    public async Task<SubLedgerMigrationReport> MigrateAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default)
    {
        var report = new SubLedgerMigrationReport(tenantId, chartId);
        var admittedAt = _time.GetUtcNow();
        var at = new Instant(admittedAt);

        // ── Step 1: Mint sub-ledger accounts by derivation ───────────────────
        var allInvoices = await _invoices.ListByChartAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false);
        var allBills    = await _bills.ListByChartAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false);

        // Derive tuples: AR → (ChartId, ArAccountId=ControlAccountId, CustomerId, Receivable)
        //                AP → (ChartId, ApAccountId=ControlAccountId, VendorId, Payable)
        await MintSubLedgerAccountsForInvoicesAsync(tenantId, chartId, allInvoices, report, at, cancellationToken).ConfigureAwait(false);
        await MintSubLedgerAccountsForBillsAsync(tenantId, chartId, allBills, report, at, cancellationToken).ConfigureAwait(false);

        // ── Step 2: Backfill SubLedgerAccountId on open items ────────────────
        await BackfillInvoicesAsync(tenantId, allInvoices, report, admittedAt, cancellationToken).ConfigureAwait(false);
        await BackfillBillsAsync(tenantId, allBills, report, admittedAt, cancellationToken).ConfigureAwait(false);

        // ── Step 3: R1 reconciliation check — surface mismatches ─────────────
        await ReconcileAsync(tenantId, chartId, report, admittedAt, cancellationToken).ConfigureAwait(false);

        return report;
    }

    // ── Private helpers — mint ────────────────────────────────────────────────

    private async Task MintSubLedgerAccountsForInvoicesAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        IReadOnlyList<Invoice> invoices,
        SubLedgerMigrationReport report,
        Instant at,
        CancellationToken ct)
    {
        // Group by (ControlAccountId=ArAccountId, CustomerId) → one sub-ledger account per tuple.
        var tuples = invoices
            .Select(i => (ControlAccountId: i.ArAccountId, PartyId: i.CustomerId))
            .Distinct()
            .ToList();

        foreach (var (controlAccountId, partyId) in tuples)
        {
            var externalRef = DeriveExternalRef(SubLedgerKind.Receivable, chartId, controlAccountId, partyId);
            var existing = await _accounts.GetByExternalRefAsync(tenantId, chartId, externalRef, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                report.AccountsSkipped++;
                continue; // idempotent
            }

            var acct = SubLedgerAccount.Create(
                tenantId: tenantId,
                chartId: chartId,
                controlAccountId: controlAccountId,
                kind: SubLedgerKind.Receivable,
                partyId: partyId,
                now: at,
                reference: $"AR — {partyId.Value}",
                externalRef: externalRef);
            await _accounts.UpsertAsync(tenantId, acct, ct).ConfigureAwait(false);
            report.AccountsMinted++;
        }
    }

    private async Task MintSubLedgerAccountsForBillsAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        IReadOnlyList<Bill> bills,
        SubLedgerMigrationReport report,
        Instant at,
        CancellationToken ct)
    {
        var tuples = bills
            .Select(b => (ControlAccountId: b.ApAccountId, PartyId: b.VendorId))
            .Distinct()
            .ToList();

        foreach (var (controlAccountId, partyId) in tuples)
        {
            var externalRef = DeriveExternalRef(SubLedgerKind.Payable, chartId, controlAccountId, partyId);
            var existing = await _accounts.GetByExternalRefAsync(tenantId, chartId, externalRef, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                report.AccountsSkipped++;
                continue;
            }

            var acct = SubLedgerAccount.Create(
                tenantId: tenantId,
                chartId: chartId,
                controlAccountId: controlAccountId,
                kind: SubLedgerKind.Payable,
                partyId: partyId,
                now: at,
                reference: $"AP — {partyId.Value}",
                externalRef: externalRef);
            await _accounts.UpsertAsync(tenantId, acct, ct).ConfigureAwait(false);
            report.AccountsMinted++;
        }
    }

    // ── Private helpers — backfill ────────────────────────────────────────────

    private async Task BackfillInvoicesAsync(
        TenantId tenantId,
        IReadOnlyList<Invoice> invoices,
        SubLedgerMigrationReport report,
        DateTimeOffset admittedAt,
        CancellationToken ct)
    {
        foreach (var inv in invoices)
        {
            if (inv.SubLedgerAccountId.HasValue)
            {
                report.InvoicesAlreadyStamped++;
                continue; // already backfilled — idempotent
            }

            var externalRef = DeriveExternalRef(SubLedgerKind.Receivable, inv.ChartId, inv.ArAccountId, inv.CustomerId);
            var acct = await _accounts.GetByExternalRefAsync(tenantId, inv.ChartId, externalRef, ct).ConfigureAwait(false);
            if (acct is null)
            {
                // No account minted for this invoice (shouldn't happen if mint ran first).
                report.InvoicesUnresolved.Add(inv.Id.Value);
                continue;
            }

            var stamped = inv with { SubLedgerAccountId = acct.Id };
            await _invoices.UpsertAsync(tenantId, stamped, admittedAt, ct).ConfigureAwait(false);
            report.InvoicesBackfilled++;
        }
    }

    private async Task BackfillBillsAsync(
        TenantId tenantId,
        IReadOnlyList<Bill> bills,
        SubLedgerMigrationReport report,
        DateTimeOffset admittedAt,
        CancellationToken ct)
    {
        foreach (var bill in bills)
        {
            if (bill.SubLedgerAccountId.HasValue)
            {
                report.BillsAlreadyStamped++;
                continue;
            }

            var externalRef = DeriveExternalRef(SubLedgerKind.Payable, bill.ChartId, bill.ApAccountId, bill.VendorId);
            var acct = await _accounts.GetByExternalRefAsync(tenantId, bill.ChartId, externalRef, ct).ConfigureAwait(false);
            if (acct is null)
            {
                report.BillsUnresolved.Add(bill.Id.Value);
                continue;
            }

            var stamped = bill with { SubLedgerAccountId = acct.Id };
            await _bills.UpsertAsync(tenantId, stamped, admittedAt, ct).ConfigureAwait(false);
            report.BillsBackfilled++;
        }
    }

    // ── Private helpers — R1 reconciliation ──────────────────────────────────

    /// <summary>
    /// Post-backfill R1 check (ADR 0120 PR-D — strengthened from PR-C):
    /// <list type="number">
    ///   <item>Per-account negative-balance check (pre-existing): accounts whose derived
    ///     balance is negative indicate an over-application or mismatched credit direction.</item>
    ///   <item>R1 cross-foot (new, PR-D): when <c>_glReadModel</c> is provided, for each
    ///     distinct <see cref="SubLedgerAccount.ControlAccountId"/> in the chart, compare
    ///     <c>Σ(derived positions, kind=K)</c> to the GL signed balance of that account. A
    ///     mismatch indicates un-stamped open items (which the FK-authoritative read now
    ///     surfaces as a zero balance on the sub-ledger rather than silently sweeping them
    ///     in via a party-wide fallback). Surfaced as <see cref="SubLedgerR1CrossFootFinding"/>s.
    ///   </item>
    /// </list>
    /// Neither mismatch class is ever silently absorbed — both are surfaced for manual review.
    /// </summary>
    private async Task ReconcileAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        SubLedgerMigrationReport report,
        DateTimeOffset admittedAt,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(admittedAt.UtcDateTime);
        var allAccounts = await _accounts.ListAllByChartAsync(tenantId, chartId, ct).ConfigureAwait(false);

        // Build the per-control-account position sums for the cross-foot (step 2).
        // Key: (ControlAccountId, Kind) → Σ derived balance.
        var controlSums = new Dictionary<(GLAccountId, SubLedgerKind), decimal>();

        // Step 1: per-account negative-balance check.
        foreach (var acct in allAccounts)
        {
            var pos = await _readModel.GetPositionAsync(tenantId, acct.Id, today, ct).ConfigureAwait(false);

            // Accumulate for cross-foot (step 2).
            var key = (acct.ControlAccountId, acct.Kind);
            controlSums[key] = controlSums.GetValueOrDefault(key) + pos.Balance;

            if (pos.Balance < 0m)
            {
                report.ReconciliationFindings.Add(new SubLedgerReconciliationFinding(
                    SubLedgerAccountId: acct.Id.Value,
                    PartyId: acct.PartyId.Value,
                    ControlAccountId: acct.ControlAccountId.Value,
                    Kind: acct.Kind,
                    ComputedBalance: pos.Balance,
                    Finding: "Negative computed balance after backfill — check for over-application or mismatched credit direction."));
            }
        }

        // Step 2: R1 cross-foot (optional — requires GL read model).
        if (_glReadModel is not null)
        {
            // GetAccountBalancesAsOfAsync returns signed raw balances (debit − credit).
            // AR control accounts are Asset-normal (debit increases); AP are Liability-normal
            // (credit increases, so the raw balance is ≤ 0 for liabilities).
            // The sub-ledger sum for AR = Σ(open-item.Balance − unapplied credits) = positive.
            // The sub-ledger sum for AP = same convention (positive outstanding payables).
            // We compare magnitudes: |Σ subledger| vs |GL signed balance|.
            var glBalances = await _glReadModel.GetAccountBalancesAsOfAsync(
                tenantId, chartId, today, snapshotMarker: string.Empty, ct).ConfigureAwait(false);

            foreach (var ((controlAccountId, kind), subledgerSum) in controlSums)
            {
                var glRaw = glBalances.GetValueOrDefault(controlAccountId, 0m);
                // AR control = Asset = debit-normal; positive raw = outstanding receivables.
                // AP control = Liability = credit-normal; the raw is negative (Credit > Debit).
                // We take |glRaw| so both kinds compare on the same sign convention as the subledger.
                var glMagnitude = Math.Abs(glRaw);

                if (Math.Abs(subledgerSum - glMagnitude) > 0.005m)
                {
                    report.R1CrossFootFindings.Add(new SubLedgerR1CrossFootFinding(
                        ControlAccountId: controlAccountId.Value,
                        Kind: kind,
                        SubLedgerSum: subledgerSum,
                        GlBalance: glMagnitude,
                        Delta: subledgerSum - glMagnitude,
                        Finding: $"R1 cross-foot mismatch for {kind} control account '{controlAccountId.Value}': " +
                                 $"Σ(sub-ledger positions) = {subledgerSum:F2}, GL balance = {glMagnitude:F2}. " +
                                 $"Possible cause: un-stamped open items not captured by backfill. " +
                                 $"Run the backfill again or investigate manually."));
                }
            }
        }
    }

    // ── Derivation key ────────────────────────────────────────────────────────

    /// <summary>
    /// Stable, deterministic derivation key for idempotent mint-or-get.
    /// Format: <c>subledger:v1:{kind}:{chartId}:{controlAccountId}:{partyId}</c>
    /// </summary>
    private static string DeriveExternalRef(
        SubLedgerKind kind,
        ChartOfAccountsId chartId,
        GLAccountId controlAccountId,
        Harborline.Api.Blocks.People.Foundation.Models.PartyId partyId)
        => $"subledger:v1:{kind}:{chartId.Value}:{controlAccountId.Value}:{partyId.Value}";
}
