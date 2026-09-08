using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>
/// Derived position of one <see cref="SubLedgerAccount"/> at a point in time.
/// Returned by <see cref="ISubLedgerReadModel.GetPositionAsync"/>.
///
/// <para>
/// <b>Derivation (C-FIN-1):</b>
/// <c>Balance = Σ(open-item.Balance) − Σ(unapplied credits)</c>.
/// <c>OpenItemBalance</c> already nets discount and write-off via
/// <c>Invoice/Bill.AmountPaid</c>. The <c>UnappliedCredits</c> component
/// accounts for <c>Payment.UnappliedAmount &gt; 0</c> (advance rent,
/// security-deposit-as-credit, vendor prepayment).
/// </para>
///
/// <para>
/// <b>Sign convention:</b>
/// For <c>Receivable</c> sub-ledgers: positive balance = amount owed to us.
/// For <c>Payable</c> sub-ledgers: positive balance = amount we owe the vendor.
/// </para>
/// </summary>
/// <param name="SubLedgerAccountId">The account this position belongs to.</param>
/// <param name="ControlAccountId">The GL control account (for R1 cross-check).</param>
/// <param name="Kind">Receivable or Payable.</param>
/// <param name="AsOf">The snapshot date.</param>
/// <param name="OpenItemBalance">
/// Sum of <c>Invoice/Bill.Balance</c> across all open items (Issued +
/// PartiallyPaid). Does NOT include Paid, Voided, or WrittenOff items
/// (those have left the open-item set and the GL reversal/bad-debt JE
/// keeps both sides balanced — C-FIN-2).
/// </param>
/// <param name="UnappliedCredits">
/// Sum of <c>Payment.UnappliedAmount</c> for all Unapplied/PartiallyApplied
/// payments associated with this sub-ledger. These are open *credit* items
/// (customer/vendor in credit relative to us).
/// </param>
/// <param name="Balance">
/// Net position: <c>OpenItemBalance − UnappliedCredits</c>.
/// </param>
public sealed record SubLedgerPosition(
    SubLedgerAccountId SubLedgerAccountId,
    GLAccountId ControlAccountId,
    SubLedgerKind Kind,
    DateOnly AsOf,
    decimal OpenItemBalance,
    decimal UnappliedCredits,
    decimal Balance)
{
    /// <summary>Zero position — useful as a default when no items exist.</summary>
    public static SubLedgerPosition Empty(
        SubLedgerAccountId id,
        GLAccountId controlAccountId,
        SubLedgerKind kind,
        DateOnly asOf)
        => new(id, controlAccountId, kind, asOf, 0m, 0m, 0m);
}
