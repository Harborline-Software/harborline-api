namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// A handler for one definition's step transitions. Given a loaded instance + the trigger that fired,
/// it decides the typed <see cref="WorkflowStepOutcome"/> — advance (optionally with an effect), park on
/// a delegated step, or complete. The <see cref="IWorkflowTriggerDispatcher"/> wraps the outcome in the
/// idempotency-guarded atomic advance, so a handler NEVER touches the store directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-definition, not general.</b> Per ADR 0135's rule-of-three re-scope, slice 1 has NO general
/// step-graph executor — each of the two v1 processes (deferred to slice 2) gets a typed handler. This
/// interface is the seam those handlers implement; the engine core supplies the durable plumbing
/// (idempotency guard, atomic advance, park/resume) around them.
/// </para>
/// <para>
/// <b>CP-park is the handler's responsibility under the v1 interim.</b> 0128 has no runtime CP/AP tag yet
/// (ADR 0135 §Prerequisites), so a handler whose step is CP (the post step; the period-touching step)
/// returns <see cref="WorkflowStepOutcome.Park"/> — hardcoded to a human-task and asserted by a
/// step-level arch-test by name (a v1 definition-of-done gate). The classification-driven dispatcher is
/// the post-PEP target. Slice 1 ships the seam; the two CP handlers + their arch-tests are slice 2.
/// </para>
/// </remarks>
public interface IWorkflowStepHandler
{
    /// <summary>The definition key this handler advances (matched against <see cref="WorkflowInstanceRecord.DefinitionKey"/>).</summary>
    string DefinitionKey { get; }

    /// <summary>
    /// Decides the outcome of applying <paramref name="trigger"/> to <paramref name="instance"/>. Pure
    /// decision logic — produces a <see cref="WorkflowStepOutcome"/>; the dispatcher commits it durably.
    /// </summary>
    ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance,
        WorkflowTrigger trigger,
        CancellationToken ct = default);
}

/// <summary>
/// The typed result of a step decision. One of: advance (commit an effect + move on), park (await a
/// delegated performer), or complete/fail (terminal advance). Constructed via the static factories.
/// </summary>
public sealed class WorkflowStepOutcome
{
    private WorkflowStepOutcome(
        WorkflowStepOutcomeKind kind,
        string nextStep,
        WorkflowStatus nextStatus,
        WorkflowEffect? effect,
        string resultJson,
        string eventType,
        string eventDataJson,
        bool isLoopBack = false)
    {
        Kind = kind;
        NextStep = nextStep;
        NextStatus = nextStatus;
        Effect = effect;
        ResultJson = resultJson;
        EventType = eventType;
        EventDataJson = eventDataJson;
        IsLoopBack = isLoopBack;
    }

    /// <summary>Which kind of outcome this is.</summary>
    public WorkflowStepOutcomeKind Kind { get; }

    /// <summary>
    /// True when this outcome is a <see cref="WorkflowStepOutcomeKind.Park"/> that RE-ENTERS an earlier step
    /// — a bounded loop-back (the invoice <c>send-back</c> round-trip). The dispatcher BUMPS the instance's
    /// durable <see cref="WorkflowInstanceRecord.Iteration"/> on a loop-back park (so the re-entered step
    /// gets a distinct idempotency key) AND enforces the max-iterations cap (ADR 0135 A0). A normal forward
    /// park (decide → the approve human-task) is NOT a loop-back and never bumps the counter. Ignored on a
    /// non-park outcome.
    /// </summary>
    public bool IsLoopBack { get; }

    /// <summary>The instance's next step after this outcome is committed.</summary>
    public string NextStep { get; }

    /// <summary>The instance's next status.</summary>
    public WorkflowStatus NextStatus { get; }

    /// <summary>The domain effect to stage atomically, or <see langword="null"/> for a pure advance.</summary>
    public WorkflowEffect? Effect { get; }

    /// <summary>The typed result recorded on the idempotency row (replayed on redelivery).</summary>
    public string ResultJson { get; }

    /// <summary>The outcome event's type string.</summary>
    public string EventType { get; }

    /// <summary>The outcome event's payload JSON.</summary>
    public string EventDataJson { get; }

    /// <summary>
    /// Advance the instance, optionally committing a domain <paramref name="effect"/> in the same
    /// transaction. <paramref name="nextStatus"/> defaults to <see cref="WorkflowStatus.Running"/>.
    /// </summary>
    public static WorkflowStepOutcome Advance(
        string nextStep,
        WorkflowEffect? effect = null,
        string resultJson = "{}",
        string eventType = "Advanced",
        string eventDataJson = "{}",
        WorkflowStatus nextStatus = WorkflowStatus.Running)
        => new(WorkflowStepOutcomeKind.Advance, nextStep, nextStatus, effect, resultJson, eventType, eventDataJson);

    /// <summary>
    /// Park the instance on a delegated step (human-task / timer / awaited dependency). The dispatcher
    /// records the park durably; the instance resumes on the performer's typed result. Used for CP steps
    /// under the v1 human-task interim.
    /// </summary>
    public static WorkflowStepOutcome Park(string step, string reasonJson = "{}")
        => new(WorkflowStepOutcomeKind.Park, step, WorkflowStatus.Parked, null, "{}", "Parked", reasonJson);

    /// <summary>
    /// Park the instance back on an EARLIER step (a bounded loop-back — the invoice <c>send-back</c>
    /// round-trip). Functionally a park, but flagged <see cref="IsLoopBack"/> so the dispatcher BUMPS the
    /// instance's durable iteration (the re-entered step then gets a distinct idempotency key) AND enforces
    /// the max-iterations cap: at the cap, the instance is ESCALATED to a terminal state instead of looping
    /// forever (ADR 0135 A0 — closes the shipped unbounded-send-back gap, bug-1353).
    /// </summary>
    public static WorkflowStepOutcome ParkLoopBack(string step, string reasonJson = "{}")
        => new(WorkflowStepOutcomeKind.Park, step, WorkflowStatus.Parked, null, "{}", "Parked", reasonJson, isLoopBack: true);

    /// <summary>Terminal advance to <see cref="WorkflowStatus.Completed"/>.</summary>
    public static WorkflowStepOutcome Complete(
        string finalStep,
        WorkflowEffect? effect = null,
        string resultJson = "{}",
        string eventDataJson = "{}")
        => new(WorkflowStepOutcomeKind.Advance, finalStep, WorkflowStatus.Completed, effect, resultJson, "Completed", eventDataJson);
}

/// <summary>The kind of a <see cref="WorkflowStepOutcome"/>.</summary>
public enum WorkflowStepOutcomeKind
{
    /// <summary>Advance the position (optionally committing an effect) in one atomic transaction.</summary>
    Advance = 0,

    /// <summary>Park durably on a delegated step and await the performer's typed result.</summary>
    Park = 1,
}
