using Harborline.Api.Blocks.FinancialPayments.Models;

namespace Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;

/// <summary>
/// Discriminates the three possible outcomes Pass 5 can record for a single
/// unapplied payment (spec §4.5 steps 3-5).
/// </summary>
public enum PaymentReconciliationOutcomeKind
{
    /// <summary>Exactly one candidate matched; the payment was applied via the applier.</summary>
    Applied = 1,

    /// <summary>Multiple candidates matched; the payment was left unapplied and surfaced for user resolution.</summary>
    Ambiguous = 2,

    /// <summary>No candidate matched; the payment was left as <c>Unapplied</c>.</summary>
    Unmatched = 3,
}

/// <summary>
/// The per-payment outcome of Pass 5 — one record per unapplied payment processed.
/// </summary>
/// <remarks>
/// Pass 6's <c>migration-report.md</c> reads these to render the "Unapplied-payment list"
/// section (spec §4.6 step 6). The ambiguous-candidates list is the user-facing
/// resolution hint.
/// </remarks>
public sealed record PaymentReconciliationOutcome
{
    /// <summary>The payment processed.</summary>
    public required PaymentId PaymentId { get; init; }

    /// <summary>Which of the three outcomes applies.</summary>
    public required PaymentReconciliationOutcomeKind Kind { get; init; }

    /// <summary>For <see cref="PaymentReconciliationOutcomeKind.Applied"/> / <see cref="PaymentReconciliationOutcomeKind.Ambiguous"/>: which document family was the target. Null for <see cref="PaymentReconciliationOutcomeKind.Unmatched"/>.</summary>
    public AppliedTo? Target { get; init; }

    /// <summary>For <see cref="PaymentReconciliationOutcomeKind.Applied"/>: the canonical id of the target invoice/bill. Null otherwise.</summary>
    public string? TargetId { get; init; }

    /// <summary>For <see cref="PaymentReconciliationOutcomeKind.Applied"/>: the amount that was applied. Null otherwise.</summary>
    public decimal? AmountApplied { get; init; }

    /// <summary>For <see cref="PaymentReconciliationOutcomeKind.Ambiguous"/>: the candidate target ids the user must choose between. Null for the other kinds.</summary>
    public IReadOnlyList<string>? AmbiguousCandidateIds { get; init; }

    public static PaymentReconciliationOutcome Applied(PaymentId paymentId, AppliedTo target, string targetId, decimal amountApplied) =>
        new()
        {
            PaymentId = paymentId,
            Kind = PaymentReconciliationOutcomeKind.Applied,
            Target = target,
            TargetId = targetId,
            AmountApplied = amountApplied,
        };

    public static PaymentReconciliationOutcome Ambiguous(PaymentId paymentId, AppliedTo target, IReadOnlyList<string> candidateIds) =>
        new()
        {
            PaymentId = paymentId,
            Kind = PaymentReconciliationOutcomeKind.Ambiguous,
            Target = target,
            AmbiguousCandidateIds = candidateIds,
        };

    public static PaymentReconciliationOutcome Unmatched(PaymentId paymentId) =>
        new()
        {
            PaymentId = paymentId,
            Kind = PaymentReconciliationOutcomeKind.Unmatched,
        };
}
