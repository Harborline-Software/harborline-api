using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0135 A1 (the deferred general interpreter) — the seam the dispatcher routes to
//  when NO per-definition IWorkflowStepHandler is registered for an instance's
//  DefinitionKey. It executes an authored + admitted + persisted WorkflowDefinition
//  generically, instead of the hand-written typed handlers (invoice / recurring / kg).
//
//  Only the SEAM lives here (blocks-workflow, so the dispatcher can call it). The IMPL
//  (DeclarativeWorkflowInterpreter) lives in a SEPARATE assembly
//  (Harborline.Api.Blocks.Workflow.Interpreter) that composes ONLY the public broker/catalog +
//  the re-validating execution store — it CANNOT name the internal effect-factory surface
//  (compiler-enforced, the SC2 F-1 containment the seam-review locked). Keeping the impl
//  out of blocks-workflow is what makes "the interpreter reaches an effect ONLY through
//  the broker" a compile-time fact rather than a source-scan.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The general declarative workflow interpreter (ADR 0135 A1). Given a loaded instance + the trigger that
/// fired, it loads the instance's PINNED, re-admitted <see cref="WorkflowDefinition"/> and decides the typed
/// <see cref="WorkflowStepOutcome"/> — advance (optionally with a broker-produced effect), park on a
/// human-task, or complete — exactly as a typed <see cref="IWorkflowStepHandler"/> would, but for ANY authored
/// definition. The <see cref="IWorkflowTriggerDispatcher"/> wraps the outcome in the same idempotency-guarded
/// atomic advance, so the interpreter never touches the store directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dispatch precedence.</b> A registered per-definition <see cref="IWorkflowStepHandler"/> ALWAYS wins for
/// its <see cref="IWorkflowStepHandler.DefinitionKey"/> (the invoice / recurring / kg handlers keep their
/// hand-audited behaviour). The interpreter is the FALLBACK the dispatcher consults only when no typed handler
/// matches — so it introduces general execution additively, without altering the shipped handlers.
/// </para>
/// <para>
/// <b>Effect boundary (SC2).</b> An effecting action flows ONLY through <see cref="IWorkflowEffectBroker"/> —
/// an AP effect autonomously, a CP effect through the SoD-gated confirm path on the human-action resume. The
/// interpreter cannot reach a factory directly (it lives in an assembly that cannot name the internal factory
/// surface). A CP effect request is re-derived from the PINNED definition + the durable instance state at
/// confirm time, never from the confirm payload (ADR 0143 F-3).
/// </para>
/// </remarks>
public interface IDeclarativeWorkflowInterpreter
{
    /// <summary>
    /// Decides the outcome of applying <paramref name="trigger"/> to <paramref name="instance"/> by executing
    /// the instance's pinned, re-admitted definition. Pure decision logic — produces a
    /// <see cref="WorkflowStepOutcome"/>; the dispatcher commits it durably. Throws
    /// <see cref="WorkflowAdmissionException"/> if the pinned definition is no longer admissible (fail-closed —
    /// it must not execute), or <see cref="WorkflowDefinitionNotFoundException"/> if it is gone.
    /// </summary>
    ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance,
        WorkflowTrigger trigger,
        WorkflowDispatchAuthority authority,
        CancellationToken ct = default);
}
