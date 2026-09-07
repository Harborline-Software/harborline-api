namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// Engine-core tuning knobs for the <see cref="WorkflowTriggerDispatcher"/> (ADR 0135 A0). Today this carries
/// only the bounded-loop guard; it is the registration seam for further engine policy without re-threading the
/// dispatcher ctor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a max-iterations cap.</b> A bounded loop-back — the shipped invoice <c>send-back</c> round-trip
/// (<c>approve → send-back → decide → approve …</c>) — has no natural terminator: each pass PARKS (writing no
/// idempotency row) and, before A0, recorded no counter, so a pathological caller could ping-pong the instance
/// forever (an unbounded loop / latent DoS, bug-1353). The cap turns the bounded loop into an actually-bounded
/// one: at <see cref="MaxIterations"/> the dispatcher ESCALATES the instance to a terminal failed state for
/// operator action instead of re-parking. The counter that the cap reads is the durable, read-back-on-resume
/// <see cref="WorkflowInstanceRecord.Iteration"/> (never recomputed — bug-1337 class), so the cap is itself
/// crash-stable.
/// </para>
/// </remarks>
public sealed record WorkflowEngineOptions
{
    /// <summary>
    /// The maximum number of loop-back ITERATIONS a single instance may take before the dispatcher escalates
    /// it to a terminal failed state. A fresh instance is iteration 0; each loop-back park bumps it. The cap is
    /// reached when a loop-back park WOULD advance the counter to (or past) this value — at which point the
    /// instance fails instead of re-parking. Must be &gt; 0.
    /// </summary>
    /// <remarks>
    /// Default 50 — generous for a legitimate human send-back round-trip (an operator revising an amount a
    /// handful of times) while still terminating a runaway loop long before any practical resource exhaustion.
    /// </remarks>
    public int MaxIterations { get; init; } = 50;

    /// <summary>The default engine options (cap = 50).</summary>
    public static WorkflowEngineOptions Default { get; } = new();

    /// <summary>Validates the options; throws if a knob is out of range. Called by the dispatcher ctor.</summary>
    public WorkflowEngineOptions Validated()
    {
        if (MaxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxIterations), MaxIterations, "MaxIterations must be greater than zero.");
        }

        return this;
    }
}
