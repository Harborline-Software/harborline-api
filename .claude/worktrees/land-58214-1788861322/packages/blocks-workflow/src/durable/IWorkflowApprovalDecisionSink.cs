using System.Threading;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0143 D-INV-7 — the override-rate health metric ("track override rate as
//  health; a persistent ~100% approve with no pushback means the gate has gone
//  blind and MUST be tightened"; the Schufa rubber-stamp rule). The broker-PEP
//  records every CP decision (confirmed vs overridden/rejected) here so the rate
//  is OWNED, not just asserted. Fail-open on the metric (recording must never
//  block a decision), fail-closed on the gate (the SoD/classification checks that
//  DO block live in the broker, not here).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>How a parked CP proposal was resolved by the human tier.</summary>
public enum WorkflowApprovalOutcome
{
    /// <summary>The human confirmed the proposal (the effect was built + staged).</summary>
    Confirmed = 0,

    /// <summary>The human rejected/overrode the proposal (no effect; a pushback signal for the rate).</summary>
    Overridden = 1,
}

/// <summary>A single CP decision recorded for the D-INV-7 override-rate metric.</summary>
/// <param name="CapabilityRef">The capability the CP op was for (e.g. <c>ledger.post-journal-entry</c>).</param>
/// <param name="InstanceId">The workflow instance the decision belongs to.</param>
/// <param name="ConfirmerPartyId">The human party that made the decision.</param>
/// <param name="Outcome">Confirmed vs Overridden.</param>
/// <param name="At">When the decision was made.</param>
public readonly record struct WorkflowApprovalDecision(
    string CapabilityRef,
    string InstanceId,
    Guid ConfirmerPartyId,
    WorkflowApprovalOutcome Outcome,
    DateTimeOffset At);

/// <summary>
/// Sink for CP approval decisions (ADR 0143 D-INV-7). The broker-PEP records every confirm/override so the
/// override rate can be surfaced as a health signal. Recording is best-effort and MUST NOT throw (a metric
/// failure can never block or unblock a security decision); the enforcing checks live in the broker.
/// </summary>
public interface IWorkflowApprovalDecisionSink
{
    /// <summary>Records one CP decision (confirm or override). Best-effort; must not throw.</summary>
    void Record(WorkflowApprovalDecision decision);
}

/// <summary>The default no-op sink (fail-open metric). A host swaps in a durable/audited sink.</summary>
public sealed class NoopWorkflowApprovalDecisionSink : IWorkflowApprovalDecisionSink
{
    /// <summary>The shared no-op instance.</summary>
    public static NoopWorkflowApprovalDecisionSink Instance { get; } = new();

    /// <inheritdoc />
    public void Record(WorkflowApprovalDecision decision)
    {
        // No-op: the null-object sink. The broker still enforces every gate regardless of the sink.
    }
}

/// <summary>
/// An in-process counting sink that OWNS the D-INV-7 override-rate metric: it tallies confirmed vs
/// overridden CP decisions and exposes the override rate. Thread-safe (interlocked counters). The default
/// registration so the rate is actually tracked out of the box; a host may replace it with a durable sink
/// that persists to the audit layer. A breach of the "meaningful review" floor (a persistent ~0% override
/// rate under load) is the D-INV-9 demotion trigger (deferred to the ADR-0130 agentic-guard amendment).
/// </summary>
public sealed class CountingWorkflowApprovalDecisionSink : IWorkflowApprovalDecisionSink
{
    private long _confirmed;
    private long _overridden;

    /// <summary>Count of CP proposals a human confirmed.</summary>
    public long ConfirmedCount => Interlocked.Read(ref _confirmed);

    /// <summary>Count of CP proposals a human overrode/rejected (the pushback signal).</summary>
    public long OverriddenCount => Interlocked.Read(ref _overridden);

    /// <summary>Total CP decisions recorded.</summary>
    public long TotalCount => ConfirmedCount + OverriddenCount;

    /// <summary>
    /// The override rate in [0,1] — <see cref="OverriddenCount"/> / <see cref="TotalCount"/>, or
    /// <c>null</c> when no decisions have been recorded (undefined, not 0). A persistent 0 under sustained
    /// load is the D-INV-7 "gone-blind" signal.
    /// </summary>
    public double? OverrideRate
    {
        get
        {
            var total = TotalCount;
            return total == 0 ? null : (double)OverriddenCount / total;
        }
    }

    /// <inheritdoc />
    public void Record(WorkflowApprovalDecision decision)
    {
        switch (decision.Outcome)
        {
            case WorkflowApprovalOutcome.Confirmed:
                Interlocked.Increment(ref _confirmed);
                break;
            case WorkflowApprovalOutcome.Overridden:
                Interlocked.Increment(ref _overridden);
                break;
        }
    }
}
