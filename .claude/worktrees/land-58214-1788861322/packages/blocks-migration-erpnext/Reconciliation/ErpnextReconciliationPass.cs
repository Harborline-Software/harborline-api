using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;

/// <summary>
/// A4.5 / importer Pass 5 — reconciliation linkage (spec §4.5). Establishes
/// payment→invoice / payment→bill links that Pass 4 did not establish (ERPNext
/// allows payments without an explicit <c>PaymentEntryReference</c>; many
/// real-world books arrive with unlinked payments).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure Anchor-side post-process — no ERPNext source consumed.</b> Pass 1-4
/// have already imported the payments + invoices + bills as Harborline-native rows.
/// Pass 5 reads those rows and heuristically links the strays. The heuristic
/// (per spec §4.5 step 2) is: same party + payment date within ±N days of the
/// document date + exact amount match. The window is configurable
/// (<see cref="ReconciliationOptions.DateWindowDays"/>) with a ±7-day baseline.
/// </para>
/// <para>
/// <b>One of three outcomes per unapplied payment</b> (spec §4.5 steps 3-5):
/// </para>
/// <list type="bullet">
///   <item>Exactly one candidate → <see cref="IReconciliationApplier.ApplyAsync"/>
///         and record <see cref="PaymentReconciliationOutcomeKind.Applied"/>.</item>
///   <item>More than one candidate → record <see cref="PaymentReconciliationOutcomeKind.Ambiguous"/>
///         with the candidate target ids; the payment stays unapplied; the user resolves via
///         the migration report.</item>
///   <item>No candidate → record <see cref="PaymentReconciliationOutcomeKind.Unmatched"/>;
///         the payment stays <c>Unapplied</c>.</item>
/// </list>
/// <para>
/// <b>Single-transaction boundary (spec §4.5 "Commit boundary").</b> The pass calls
/// <see cref="IReconciliationApplier.ApplyAsync"/> per unique match; the
/// composition-root applier is responsible for wrapping the per-pass invocations
/// in a single SQLite transaction. The composition-root adapter (delegated to the
/// already-shipped <c>IPaymentApplicationService</c>, which owns the persistence +
/// validation) lands with the A7 orchestrator (PR-3), which owns the outer
/// transaction scope.
/// </para>
/// <para>
/// <b>Amount-match semantics.</b> The candidate filter compares
/// <c>payment.Amount</c> to <c>invoice.Total</c> / <c>bill.Total</c> (the
/// full-payment-pays-full-document ERPNext scenario the spec is targeting).
/// Partial-application cases — where the user paid two invoices with one check
/// — are intentionally out of v1 heuristic scope; they surface as
/// <see cref="PaymentReconciliationOutcomeKind.Unmatched"/> for CO resolution
/// via the migration report (Pass 6).
/// </para>
/// </remarks>
public sealed class ErpnextReconciliationPass
{
    private readonly IPaymentRepository _payments;
    private readonly IInvoiceRepository _invoices;
    private readonly IBillRepository _bills;
    private readonly IReconciliationApplier _applier;

    public ErpnextReconciliationPass(
        IPaymentRepository payments,
        IInvoiceRepository invoices,
        IBillRepository bills,
        IReconciliationApplier applier)
    {
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
        _bills = bills ?? throw new ArgumentNullException(nameof(bills));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    /// <summary>
    /// Run Pass 5 over the supplied tenant + chart.
    /// </summary>
    /// <param name="tenantId">The tenant whose payments are reconciled.</param>
    /// <param name="chartId">The chart-of-accounts the import landed against.</param>
    /// <param name="options">Heuristic options (date window); defaults to <see cref="ReconciliationOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-payment outcome record set for Pass 6's report.</returns>
    public async Task<ReconciliationPassResult> RunAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        ReconciliationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ReconciliationOptions.Default;
        var dateWindow = options.DateWindowDays;

        // Load payments + filter to the unapplied set (UnappliedAmount > 0).
        var allPayments = await _payments.ListByChartAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false);
        var unappliedPayments = allPayments.Where(p => p.UnappliedAmount > 0m).ToList();

        // Load open invoices (Balance > 0) — candidates for inbound payments.
        var openInvoices = (await _invoices.ListByChartAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false))
            .Where(i => i.Balance > 0m)
            .ToList();

        // Load open bills (Balance > 0) — candidates for outbound payments.
        var openBills = (await _bills.ListByChartAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false))
            .Where(b => b.Balance > 0m)
            .ToList();

        var outcomes = new List<PaymentReconciliationOutcome>(unappliedPayments.Count);

        foreach (var payment in unappliedPayments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PaymentReconciliationOutcome outcome = payment.Direction switch
            {
                PaymentDirection.Inbound => await ReconcileInboundAsync(payment, openInvoices, dateWindow, cancellationToken).ConfigureAwait(false),
                PaymentDirection.Outbound => await ReconcileOutboundAsync(payment, openBills, dateWindow, cancellationToken).ConfigureAwait(false),
                _ => PaymentReconciliationOutcome.Unmatched(payment.Id),
            };

            outcomes.Add(outcome);
        }

        return new ReconciliationPassResult(outcomes);
    }

    private async Task<PaymentReconciliationOutcome> ReconcileInboundAsync(
        Payment payment,
        IReadOnlyList<Invoice> openInvoices,
        int dateWindowDays,
        CancellationToken cancellationToken)
    {
        var candidates = openInvoices
            .Where(i =>
                i.CustomerId == payment.PartyId &&
                WithinDateWindow(i.IssueDate, payment.PaymentDate, dateWindowDays) &&
                i.Total == payment.Amount)
            .ToList();

        return await ResolveAsync(payment, candidates, AppliedTo.Invoice, i => i.Id.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PaymentReconciliationOutcome> ReconcileOutboundAsync(
        Payment payment,
        IReadOnlyList<Bill> openBills,
        int dateWindowDays,
        CancellationToken cancellationToken)
    {
        var candidates = openBills
            .Where(b =>
                b.VendorId == payment.PartyId &&
                WithinDateWindow(b.BillDate, payment.PaymentDate, dateWindowDays) &&
                b.Total == payment.Amount)
            .ToList();

        return await ResolveAsync(payment, candidates, AppliedTo.Bill, b => b.Id.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PaymentReconciliationOutcome> ResolveAsync<T>(
        Payment payment,
        IReadOnlyList<T> candidates,
        AppliedTo target,
        Func<T, string> targetIdOf,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 1)
        {
            var targetId = targetIdOf(candidates[0]);
            var applied = await _applier
                .ApplyAsync(payment.TenantId, payment.Id, target, targetId, payment.UnappliedAmount, cancellationToken)
                .ConfigureAwait(false);

            return applied
                ? PaymentReconciliationOutcome.Applied(payment.Id, target, targetId, payment.UnappliedAmount)
                // The applier rejected the apply (e.g., balance changed mid-pass) — count as Unmatched
                // so the payment stays Unapplied and Pass 6 surfaces it. Distinguishing applier-reject
                // from no-match is a future refinement; v1 collapses them so the report has a single
                // "needs attention" bucket.
                : PaymentReconciliationOutcome.Unmatched(payment.Id);
        }

        if (candidates.Count > 1)
        {
            var ids = candidates.Select(targetIdOf).ToList();
            return PaymentReconciliationOutcome.Ambiguous(payment.Id, target, ids);
        }

        return PaymentReconciliationOutcome.Unmatched(payment.Id);
    }

    private static bool WithinDateWindow(DateOnly documentDate, DateOnly paymentDate, int windowDays) =>
        Math.Abs(documentDate.DayNumber - paymentDate.DayNumber) <= windowDays;
}
