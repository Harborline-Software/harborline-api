using Harborline.Api.Blocks.Banking.Models;

namespace Harborline.Api.Blocks.Banking.Matching;

/// <summary>
/// A detected inter-account transfer pair: an outflow on one of the operator's own
/// accounts and a corresponding inflow on another, representing a single economic
/// transfer that MUST NOT be double-booked as two independent ledger postings.
/// Per ADR 0112 fin-acct C4 — double-count prevention.
/// </summary>
/// <remarks>
/// <para>
/// Pairing criteria: opposite-sign, same magnitude (within <see cref="TransferPairingDetector.AmountToleranceDecimal"/>),
/// near-date (within <see cref="TransferPairingDetector.DateWindowDays"/>) across two of the
/// operator's own <see cref="BankAccount"/>s.
/// </para>
/// <para>
/// The matching engine MUST propose the two legs as a single transfer/contra
/// (one ledger transaction) rather than two independent proposals. The pair
/// lines are presented together to the human; the human confirms once.
/// </para>
/// </remarks>
/// <param name="OutflowLine">The statement line with a negative amount (outflow from account A).</param>
/// <param name="InflowLine">The statement line with a positive amount (inflow to account B).</param>
public sealed record TransferPair(
    StatementLine OutflowLine,
    StatementLine InflowLine);
