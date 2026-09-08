using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// Routes a <see cref="WorkflowTrigger"/> (one of the four ADR 0135 D1 triggers) to the
/// <see cref="IWorkflowStepHandler"/> bound for the target instance's definition, and commits the
/// handler's outcome durably through the idempotency-guarded atomic advance.
/// </summary>
public interface IWorkflowTriggerDispatcher
{
    /// <summary>
    /// Dispatches <paramref name="trigger"/>: loads the instance, replays if the step already advanced
    /// (idempotency hit → no-op), else asks the handler for an outcome and commits it atomically.
    /// </summary>
    /// <returns>The dispatch result — what happened (advanced / parked / replayed / unknown-instance).</returns>
    Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, CancellationToken ct = default);

    /// <summary>Dispatches a synchronous reaction with the immutable decision that admitted its originating act.</summary>
    Task<WorkflowDispatchResult> DispatchAsync(
        WorkflowTrigger trigger,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default);

    /// <summary>
    /// Dispatches a synchronous reaction with the workflow-record decision and, when the selected
    /// definition can create a domain record, the independently decided authority for that exact record.
    /// </summary>
    Task<WorkflowDispatchResult> DispatchAsync(
        WorkflowTrigger trigger,
        WorkflowDispatchAuthority authority,
        CancellationToken ct = default);
}

/// <summary>
/// Immutable authorization evidence carried from the route/scheduler into one workflow act. The workflow
/// decision admits the instance transition; <see cref="EffectDecision"/> admits the exact record created by
/// a synchronous effect. Consumers verify these decisions and never re-decide them.
/// </summary>
public sealed record WorkflowDispatchAuthority(
    AuthorizationDecision WorkflowDecision,
    AuthorizationDecision? EffectDecision = null);

/// <summary>The outcome of one workflow trigger dispatch.</summary>
public enum WorkflowDispatchResult
{
    /// <summary>The instance advanced (an effect may have committed).</summary>
    Advanced = 0,

    /// <summary>The instance parked on a delegated step and awaits its performer.</summary>
    Parked = 1,

    /// <summary>The step had already advanced — the recorded result was replayed; nothing new committed.</summary>
    ReplayedNoOp = 2,

    /// <summary>No instance with that id exists.</summary>
    UnknownInstance = 3,

    /// <summary>The instance is in a terminal state (Completed / Failed) — the trigger was ignored.</summary>
    Terminal = 4,
}
