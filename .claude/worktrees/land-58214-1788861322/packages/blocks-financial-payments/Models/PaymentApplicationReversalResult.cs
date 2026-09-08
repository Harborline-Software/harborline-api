namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// What a call to <c>IPaymentApplicationRepository.ReverseAsync</c> actually did.
/// </summary>
/// <remarks>
/// Ticket 095. A nullable <see cref="PaymentApplication"/> collapsed
/// <see cref="Recorded"/> and <see cref="AlreadyReversed"/> into one non-null signal, so a caller
/// could be told it reversed an application that a concurrent caller had already reversed — and
/// then restore a balance a second time against contra evidence it did not write.
/// </remarks>
public enum PaymentApplicationReversalOutcome
{
    /// <summary>The id is unknown, cross-tenant, or names a contra row. Nothing was written.</summary>
    NotFound = 0,

    /// <summary>THIS call wrote the contra row and stamped the original.</summary>
    Recorded = 1,

    /// <summary>The original was already reversed by an earlier call. This call wrote nothing.</summary>
    AlreadyReversed = 2,
}

/// <summary>
/// Discriminated outcome of a reversal attempt, carrying the reversed original when there is one.
/// </summary>
/// <param name="Outcome">Which of the three branches the repository took.</param>
/// <param name="Application">
/// The reversed original for <see cref="PaymentApplicationReversalOutcome.Recorded"/> and
/// <see cref="PaymentApplicationReversalOutcome.AlreadyReversed"/>; <c>null</c> for
/// <see cref="PaymentApplicationReversalOutcome.NotFound"/>.
/// </param>
public readonly record struct PaymentApplicationReversalResult(
    PaymentApplicationReversalOutcome Outcome,
    PaymentApplication? Application)
{
    /// <summary>Nothing to reverse: unknown id, foreign tenant, or a contra row.</summary>
    public static PaymentApplicationReversalResult NotFound { get; }
        = new(PaymentApplicationReversalOutcome.NotFound, null);

    /// <summary>This call wrote the contra evidence.</summary>
    public static PaymentApplicationReversalResult Recorded(PaymentApplication reversed)
        => new(PaymentApplicationReversalOutcome.Recorded, reversed);

    /// <summary>An earlier call wrote the contra evidence; this one wrote nothing.</summary>
    public static PaymentApplicationReversalResult AlreadyReversed(PaymentApplication existing)
        => new(PaymentApplicationReversalOutcome.AlreadyReversed, existing);

    /// <summary>True only when THIS call wrote contra evidence — the licence to restore a balance.</summary>
    public bool WroteEvidence => Outcome == PaymentApplicationReversalOutcome.Recorded;
}
