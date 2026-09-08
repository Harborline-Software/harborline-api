namespace Harborline.Api.Blocks.FinancialSubLedger.Models;

/// <summary>
/// Axis classification of a <see cref="SubLedgerAccount"/> — mirrors the
/// <c>PaymentDirection</c> axis (Inbound = Receivable; Outbound = Payable)
/// and the Invoice/Bill split.
///
/// <para>
/// <b>R1 per-kind invariant:</b> AR sub-ledgers reconcile to an Asset-class
/// GL control account; AP sub-ledgers reconcile to a Liability-class GL
/// control account. Mixing kinds across a control account violates the
/// reconciliation invariant (see <c>ISubLedgerReadModel</c> contract and
/// the D3 pack-boundary arch-test in <c>SubLedgerPackBoundaryArchitectureTests</c>).
/// </para>
/// </summary>
public enum SubLedgerKind
{
    /// <summary>
    /// Accounts receivable subsidiary account. Open items are <c>Invoice</c>
    /// records; control account is an <c>Asset</c>-type GL account.
    /// </summary>
    Receivable,

    /// <summary>
    /// Accounts payable subsidiary account. Open items are <c>Bill</c>
    /// records; control account is a <c>Liability</c>-type GL account.
    /// </summary>
    Payable,
}
