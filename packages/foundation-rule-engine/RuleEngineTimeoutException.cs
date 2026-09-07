namespace Harborline.Api.Foundation.RuleEngine;

/// <summary>
/// The <b>non-authoritative liveness guard</b> for a single evaluation (SPINE-1 design §4,
/// as amended by the D1 ratification 2026-07-01).
/// </summary>
/// <remarks>
/// <para>
/// The per-evaluation wall-clock ceiling (and any external <see cref="System.Threading.CancellationToken"/>)
/// is time- and hardware-dependent: it can trip on the slower .NET tier but not the faster TS tier — or
/// differently on replay / future hardware. If a wall-clock trip produced an evaluation <i>outcome</i>
/// (a <c>rule.timeout</c> <see cref="Model.Validity"/>/<see cref="Model.ComputedValue"/>), the two tiers
/// would emit divergent <see cref="Model.RuleOutcome"/> values for the same input, breaking
/// replay-determinism and the byte-identical conformance corpus.
/// </para>
/// <para>
/// Therefore the wall-clock is NOT outcome-affecting. The <b>op-budget</b>
/// (<see cref="RuleEngineLimits.StepBudget"/> → <c>rule.budget_exceeded</c>) is the SOLE authoritative,
/// deterministic, outcome-affecting bound. When the wall-clock (or a cancellation token) trips, the engine
/// throws THIS infrastructure exception — identical in kind on both tiers (the TS tier throws
/// <c>RuleTimeout</c>) — instead of returning a divergent evaluation result. Callers treat it as an
/// infrastructure fault (fail the write closed / retry / degrade the render), never as a rule verdict.
/// </para>
/// </remarks>
public sealed class RuleEngineTimeoutException : Exception
{
    /// <summary>The stable diagnostic code for this infrastructure fault (<c>rule.timeout</c>).</summary>
    public string Code => RuleEngineCodes.Timeout;

    /// <summary>Creates the timeout with the default message.</summary>
    public RuleEngineTimeoutException()
        : base("Rule evaluation exceeded the wall-clock liveness ceiling or was cancelled. "
             + "This is a non-authoritative infrastructure fault, not an evaluation outcome.")
    {
    }
}
