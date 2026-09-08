using System.Globalization;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  The durable process model — the 3 store tables (ADR 0135 D2) as provider-neutral
//  POCOs, plus the lifecycle status, the D7 definition version, and the trigger model.
//
//  These types live in blocks-workflow (slice 2 — the engine core) and carry NO EF
//  dependency. The recoverable EF/SQLite store (apps/local-node-host) maps them onto
//  LocalNodeDbContext so {effect + outcome event + idempotency row + position} co-commit
//  in ONE SQLite transaction (build invariant #1).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The durable process instance: <c>{definition, current position, status}</c> + the
/// <b>pinned definition version</b> (ADR 0135 D7). Maps to the <c>workflow_instances</c> table.
/// </summary>
/// <remarks>
/// A <b>Process</b> in ADR 0135's vocabulary (FE-3) — a running instance a user watches/advances.
/// The instance row is the single-owner-serialized non-monotonic state (ADR 0135 D3): only the tenant
/// home advances it. Mutable by design — each advance updates <see cref="CurrentStep"/> +
/// <see cref="Status"/> in the same transaction as the effect.
/// </remarks>
public sealed class WorkflowInstanceRecord
{
    /// <summary>Stable instance identifier (the home-serialized owner key).</summary>
    public required string Id { get; set; }

    /// <summary>The tenant this instance is homed to (ADR 0092 defence-in-depth boundary on the node).</summary>
    public required string TenantId { get; set; }

    /// <summary>The <b>Workflow</b> (definition/template) key this Process is an instance of.</summary>
    public required string DefinitionKey { get; set; }

    /// <summary>
    /// The definition version pinned <b>at instantiation</b> (ADR 0135 D7). A locked instance always
    /// resolves replay against THIS version — a replayed step must be deterministic, so it cannot
    /// re-resolve against a newer definition. An editable instance re-pins on each human edit (each edit
    /// is a new effective-dated version); already-dispatched steps are never retroactively altered.
    /// </summary>
    public required string DefinitionVersion { get; set; }

    /// <summary>The step the instance is parked at / about to run. Single-owner serialized.</summary>
    public required string CurrentStep { get; set; }

    /// <summary>
    /// The instance's CURRENT iteration counter (ADR 0135 A0 — real iteration). The
    /// <see cref="WorkflowStepKey"/> for an advancing step is derived from THIS value, so a step that
    /// is RE-ENTERED via a bounded loop (the invoice <c>send-back</c> round-trip) gets a distinct,
    /// deterministic, crash-stable idempotency key on each pass — <c>(instance, iteration, step)</c> —
    /// instead of colliding at iteration 0.
    /// </summary>
    /// <remarks>
    /// <b>Durable, read-back-on-resume — NEVER recomputed (bug-1337 class).</b> This counter is persisted
    /// on the instance row and read back verbatim on <see cref="IWorkflowStore.LoadAsync"/>; it is NOT
    /// derived from the event log on resume. Recomputing it (e.g. counting send-back events) is exactly the
    /// bug-1337 failure mode — a resume that re-derives an id can desync across a crash or across machines.
    /// The counter advances ONLY on a loop-back park (a step re-entering an earlier step), bumped
    /// atomically with that park (<see cref="IWorkflowStore.ParkAsync"/>). A forward CP-park (decide → the
    /// approve human-task) does NOT bump it — it is not a re-entry. Starts at 0.
    /// </remarks>
    public int Iteration { get; set; }

    /// <summary>Lifecycle status.</summary>
    public WorkflowStatus Status { get; set; }

    /// <summary>Opaque JSON working state carried by the instance.</summary>
    public string StateJson { get; set; } = "{}";

    /// <summary>UTC instant the instance was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC instant of the most recent advance (equals <see cref="CreatedAt"/> before any advance).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Lifecycle status of a durable process instance.</summary>
public enum WorkflowStatus
{
    /// <summary>Active — the next automated step may run.</summary>
    Running = 0,

    /// <summary>
    /// Parked on a delegated step (human-task, timer, or an awaited dependency); resumes on the
    /// performer's typed result. CP steps park here under the v1 human-task interim (ADR 0135 §Prerequisites).
    /// </summary>
    Parked = 1,

    /// <summary>Terminal — the instance reached an end state.</summary>
    Completed = 2,

    /// <summary>Terminal — the instance failed and will not advance further without operator action.</summary>
    Failed = 3,
}

/// <summary>
/// An append-only history row (ADR 0135 D2 — the <c>workflow_events</c> table). The grow-only event log
/// is the monotonic, CRDT-safe part of the model (ADR 0135 D3); the <c>(InstanceId, Seq)</c> uniqueness
/// invariant makes it a totally-ordered-per-instance log.
/// </summary>
public sealed class WorkflowEventRecord
{
    /// <summary>Store-assigned global autoincrement (the physical append order).</summary>
    public long Id { get; set; }

    /// <summary>The instance this event belongs to.</summary>
    public required string InstanceId { get; set; }

    /// <summary>
    /// The per-instance monotonic sequence number. <c>(InstanceId, Seq)</c> is UNIQUE — the append-only
    /// invariant the engine relies on for deterministic replay (ADR 0135 SC1 audited history).
    /// </summary>
    public long Seq { get; set; }

    /// <summary>The step that produced this event.</summary>
    public required string Step { get; set; }

    /// <summary>The event kind (e.g. <c>"Advanced"</c>, <c>"Parked"</c>, <c>"Resumed"</c>, <c>"TriggerFired"</c>).</summary>
    public required string EventType { get; set; }

    /// <summary>Opaque JSON event payload.</summary>
    public string DataJson { get; set; } = "{}";

    /// <summary>UTC instant the event occurred.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// The per-<c>(instance, iteration, step)</c> idempotency record (ADR 0135 D2 — the
/// <c>workflow_step_idempotency</c> table). Its PRIMARY KEY is the
/// <see cref="WorkflowStepKey.Value"/> string; the mere PRESENCE of the row means "this step already
/// advanced" — the durable proof that turns a redelivered trigger or post-crash resume into a no-op.
/// Co-committed with the effect (that co-commit IS build invariant #1).
/// </summary>
public sealed class WorkflowStepIdempotencyRecord
{
    /// <summary>The stable idempotency key — <see cref="WorkflowStepKey.Value"/>. PK.</summary>
    public required string Key { get; set; }

    /// <summary>The instance this step belongs to.</summary>
    public required string InstanceId { get; set; }

    /// <summary>The step identifier.</summary>
    public required string Step { get; set; }

    /// <summary>The 0-based iteration of the step.</summary>
    public int Iteration { get; set; }

    /// <summary>
    /// The typed result the step produced (e.g. the posted JE id). On a redelivered trigger the engine
    /// REPLAYS this recorded result rather than re-running the step (ADR 0135 §Idempotency — replay reuses
    /// recorded outputs, never re-calls models).
    /// </summary>
    public string ResultJson { get; set; } = "{}";

    /// <summary>UTC instant the step completed.</summary>
    public DateTimeOffset CompletedAt { get; set; }
}

/// <summary>
/// The four triggers that advance a process instance (ADR 0135 D1). A pure automation rule is the
/// degenerate 1-step case; the engine differs by trigger, horizon, and shape, not by being four engines.
/// </summary>
public enum WorkflowTriggerKind
{
    /// <summary>A domain event arrived (e.g. <c>invoice.issued</c>) — reactive ECA.</summary>
    Event = 0,

    /// <summary>A scheduled wall-clock / RRULE occurrence is due (the daemon trigger).</summary>
    Schedule = 1,

    /// <summary>A human submitted a typed result for a parked human-task (approve / reject / send-back).</summary>
    HumanAction = 2,

    /// <summary>A dependency (a prerequisite step / sub-process) completed (Gantt-style join).</summary>
    DependencyComplete = 3,
}

/// <summary>
/// A trigger occurrence routed to a specific instance + step. The <see cref="IWorkflowTriggerDispatcher"/>
/// routes it to the handler bound for the instance's definition, which produces a typed
/// <see cref="WorkflowStepOutcome"/>.
/// </summary>
/// <param name="Kind">Which of the four triggers fired.</param>
/// <param name="InstanceId">The instance to advance.</param>
/// <param name="Step">The step the trigger targets.</param>
/// <param name="PayloadJson">Opaque JSON trigger payload (e.g. the human's decision, the event body).</param>
/// <param name="At">The single operation instant captured by the invoking boundary.</param>
public readonly record struct WorkflowTrigger(
    WorkflowTriggerKind Kind,
    string InstanceId,
    string Step,
    string PayloadJson,
    DateTimeOffset At)
{
    /// <summary>
    /// The immutable decision carried by the dispatcher for a synchronous reaction. Typed handlers pass
    /// this value into effects that audit the admitted act; they never resolve or re-decide authority.
    /// </summary>
    public AuthorizationDecision? AdmittedDecision { get; init; }

    /// <summary>Convenience factory requiring the instant captured by the invoking boundary.</summary>
    public static WorkflowTrigger For(
        WorkflowTriggerKind kind,
        string instanceId,
        string step,
        DateTimeOffset at,
        string payloadJson = "{}")
        => new(kind, instanceId, step, payloadJson, at);

    // Kept internal for legacy unit fixtures only. Production callers cannot omit the boundary instant.
    internal static WorkflowTrigger For(
        WorkflowTriggerKind kind,
        string instanceId,
        string step,
        string payloadJson = "{}")
        => For(kind, instanceId, step, DateTimeOffset.UnixEpoch, payloadJson);
}
