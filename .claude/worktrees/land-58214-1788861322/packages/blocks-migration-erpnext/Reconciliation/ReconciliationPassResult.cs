namespace Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;

/// <summary>
/// The complete result of one run of <see cref="ErpnextReconciliationPass"/>.
/// </summary>
/// <remarks>
/// Carries every per-payment outcome (one record per unapplied payment processed)
/// plus aggregate counts. Pass 6 reads this to render the report's
/// "Unapplied-payment list" + reconciliation summary sections (spec §4.6 step 6).
/// </remarks>
public sealed record ReconciliationPassResult
{
    public ReconciliationPassResult(IReadOnlyList<PaymentReconciliationOutcome> outcomes)
    {
        Outcomes = outcomes ?? throw new ArgumentNullException(nameof(outcomes));
    }

    public IReadOnlyList<PaymentReconciliationOutcome> Outcomes { get; }

    public int AppliedCount => Outcomes.Count(o => o.Kind == PaymentReconciliationOutcomeKind.Applied);
    public int AmbiguousCount => Outcomes.Count(o => o.Kind == PaymentReconciliationOutcomeKind.Ambiguous);
    public int UnmatchedCount => Outcomes.Count(o => o.Kind == PaymentReconciliationOutcomeKind.Unmatched);
    public int TotalProcessed => Outcomes.Count;
}
