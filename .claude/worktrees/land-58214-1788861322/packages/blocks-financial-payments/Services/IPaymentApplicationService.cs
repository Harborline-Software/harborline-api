using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.FinancialPayments.Services;

// ──────────────────────────────────────────────────────────────────────────────
// Result / error types
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Error discriminant for <see cref="IPaymentApplicationService.ApplyAsync"/>.</summary>
public enum ApplyError
{
    None = 0,
    UnknownPayment,
    UnknownTarget,

    /// <summary>
    /// Direction-matching invariant violated: Inbound → Bill or Outbound → Invoice.
    /// This MUST be checked BEFORE repository lookups to prevent timing attacks
    /// that could reveal target existence by observing error-type differences.
    /// </summary>
    DirectionMismatch,

    /// <summary><c>amountApplied &gt; Payment.UnappliedAmount</c>.</summary>
    InsufficientUnapplied,

    /// <summary><c>amountApplied + discountAmount + writeoffAmount &gt; target.Balance</c>.</summary>
    TargetBalanceInsufficient,

    /// <summary>Payment and target have different <c>Currency</c> values.</summary>
    CurrencyMismatch,

    /// <summary>Target Invoice/Bill is in a terminal state (Voided, WrittenOff, etc.).</summary>
    TargetTerminal,

    /// <summary>The payment has no posted clearing journal and cannot reduce a subledger balance.</summary>
    PaymentNotCleared,
}

/// <summary>Error discriminant for <see cref="IPaymentApplicationService.UnapplyAsync"/>.</summary>
public enum UnapplyError
{
    None = 0,
    UnknownApplication,

    /// <summary>
    /// No fiscal period covers the REVERSAL date. The contra evidence has nowhere to land, so the
    /// unapply is refused rather than writing a row outside every period.
    /// </summary>
    NoPeriodForReversalDate,

    /// <summary>
    /// The period covering the REVERSAL date is <see cref="IPeriodResolver.Status.Locked"/>. Note
    /// this gates the reversal date, never the original application's period — a locked past period
    /// must stay correctable, and the contra always lands in the current one.
    /// </summary>
    ReversalPeriodLocked,

    /// <summary>
    /// The period covering the reversal date is <see cref="IPeriodResolver.Status.SoftClosed"/> and
    /// the caller does not hold the soft-close override permission.
    /// </summary>
    ReversalPeriodSoftClosed,

    /// <summary>
    /// The repository did not record the reversal — the application vanished, or it is itself
    /// reversal evidence. Nothing was mutated: the reversal is attempted BEFORE any balance is
    /// restored precisely so this case cannot leave restored balances without contra evidence.
    /// </summary>
    ReversalNotRecorded,
}

/// <summary>Result of <see cref="IPaymentApplicationService.ApplyAsync"/>.</summary>
public sealed record ApplyResult(PaymentApplication? Application, ApplyError Error, string? ErrorMessage);

/// <summary>Result of <see cref="IPaymentApplicationService.UnapplyAsync"/>.</summary>
public sealed record UnapplyResult(bool Success, UnapplyError Error, string? ErrorMessage);

// ──────────────────────────────────────────────────────────────────────────────
// Service interface (stub — implemented in PR 3; SECURITY SPOT-CHECK REQUIRED)
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// High-level application service that applies or reverses a
/// <see cref="PaymentApplication"/> and keeps all downstream balances
/// consistent.
///
/// <para>
/// <b>Declared here (PR 1) so downstream can reference the interface at compile
/// time.</b> <c>DefaultPaymentApplicationService</c> ships in PR 3 — which
/// requires a security spot-check before merging (direction-matching invariant
/// is a financial correctness gate).
/// </para>
///
/// <para>
/// <b>On success, ApplyAsync:</b>
/// <list type="number">
///   <item>Creates a <see cref="PaymentApplication"/> record.</item>
///   <item>Updates Invoice/Bill: <c>AmountPaid += amountApplied</c>; recomputes balance + status.</item>
///   <item>Updates Payment: <c>UnappliedAmount -= amountApplied</c>; recomputes status.</item>
///   <item>Emits <c>Financial.PaymentApplied</c> audit event.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>On success, UnapplyAsync:</b>
/// <list type="number">
///   <item>Retains the original <see cref="PaymentApplication"/> and adds linked contra evidence.</item>
///   <item>Restores Invoice/Bill: <c>AmountPaid -= amountApplied</c>; recomputes balance + status.</item>
///   <item>Restores Payment: <c>UnappliedAmount += amountApplied</c>; recomputes status.</item>
///   <item>Emits <c>Financial.PaymentUnapplied</c> audit event.</item>
/// </list>
/// </para>
/// </summary>
public interface IPaymentApplicationService
{
    /// <summary>
    /// Apply <paramref name="amountApplied"/> of <paramref name="paymentId"/> to
    /// <paramref name="targetId"/> (Invoice or Bill, discriminated by <paramref name="appliedTo"/>).
    ///
    /// <para>Direction-matching is checked FIRST — before any repository lookup.</para>
    /// </summary>
    Task<ApplyResult> ApplyAsync(
        PaymentId paymentId,
        AppliedTo appliedTo,
        string targetId,
        decimal amountApplied,
        decimal discountAmount,
        decimal writeoffAmount,
        PartyId actor,
        CancellationToken ct = default);

    /// <summary>
    /// Reverse a specific application (correction path). Restores balances on the
    /// Invoice/Bill and on the Payment while retaining linked allocation evidence.
    /// </summary>
    /// <param name="applicationId">The application to reverse.</param>
    /// <param name="actor">The acting party the restored rows are STAMPED with (attribution).</param>
    /// <param name="authority">
    /// The server-derived authority this act is DECIDED with (ticket 205): the request principal the
    /// reversal-date period gate is resolved about, the tenant the act is scoped to, and the admitted
    /// instant. Deliberately distinct from <paramref name="actor"/> — the grant subject and the
    /// attribution are two different identities on a selected-session request. Naming it is not optional:
    /// a caller cannot reach the soft-close override without saying who is overriding and when.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<UnapplyResult> UnapplyAsync(
        PaymentApplicationId applicationId,
        PartyId actor,
        Harborline.Api.Foundation.Authorization.AuthorizationWriteContext authority,
        CancellationToken ct = default);
}
