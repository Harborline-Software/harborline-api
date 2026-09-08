namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// The provider-neutral durable-process-store seam (ADR 0135 D2). The engine core (slice 2 of
/// <c>blocks-workflow</c>) depends only on this contract; the recoverable EF/SQLite implementation
/// (<c>apps/local-node-host</c>, <c>NodeEfWorkflowStore</c>) rides the existing
/// <c>LocalNodeDbContext</c> + the ADR-0126 enlister so a step's effect, its outcome event, its
/// idempotency row, and the instance position co-commit in ONE store transaction
/// (<b>build invariant #1</b> — atomic advance).
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam is deliberately small.</b> Per ADR 0135's rule-of-three re-scope, slice 1 builds the
/// irreducible core (instances / events / idempotency + atomic advance + park/resume + the 4 triggers)
/// and DEFERS the general step-graph executor + saga runner until a third real waiting-case lands. The
/// contract therefore exposes the few operations whose <b>atomicity</b> is load-bearing — not a general
/// step-graph API.
/// </para>
/// <para>
/// <b>Effect-agnostic advance.</b> <see cref="AdvanceAsync"/> takes a <see cref="WorkflowEffect"/> — an
/// opaque enlistment the impl stages onto its in-flight unit-of-work BEFORE the single commit — so the
/// engine core never references a domain effect type (a posted JE lives in the EF impl's effect, not in
/// this seam). This keeps <c>blocks-workflow</c> free of any financial-cluster dependency.
/// </para>
/// </remarks>
public interface IWorkflowStore
{
    /// <summary>Loads an instance by id, or <see langword="null"/> if unknown.</summary>
    Task<WorkflowInstanceRecord?> LoadAsync(string instanceId, CancellationToken ct = default);

    /// <summary>Creates a new instance row (the start of a Process). Single write.</summary>
    Task CreateInstanceAsync(
        WorkflowInstanceRecord instance,
        DateTimeOffset at,
        CancellationToken ct = default);

    /// <summary>
    /// Idempotency lookup — has this <c>(instance, iteration, step)</c> already advanced? Returns the
    /// recorded result if so (the engine REPLAYS it rather than re-running the step), else
    /// <see langword="null"/>.
    /// </summary>
    Task<WorkflowStepIdempotencyRecord?> FindStepResultAsync(WorkflowStepKey key, CancellationToken ct = default);

    /// <summary>
    /// <b>ATOMIC ADVANCE (build invariant #1).</b> Co-commits, in ONE store transaction:
    /// <list type="number">
    ///   <item>the optional domain <paramref name="effect"/> (staged onto the same unit-of-work),</item>
    ///   <item>an append-only outcome <see cref="WorkflowEventRecord"/> (next per-instance <c>Seq</c>),</item>
    ///   <item>the per-<c>(instance, iteration, step)</c> idempotency row,</item>
    ///   <item>the instance position/status update.</item>
    /// </list>
    /// If <paramref name="effect"/> throws while staging (or the commit fails), NOTHING commits — the
    /// effect rolls back with the event + idempotency row + position, so a crash in the window leaves the
    /// store exactly as before and the resume is a no-op (ADR 0135 SC1).
    /// </summary>
    /// <param name="key">The step's stable idempotency key.</param>
    /// <param name="effect">
    /// The domain effect to stage atomically, or <see langword="null"/> for a pure advance (e.g. recording
    /// a human decision with no side effect). The impl invokes it to stage rows onto the in-flight context.
    /// </param>
    /// <param name="resultJson">The typed result recorded on the idempotency row (replayed on redelivery).</param>
    /// <param name="eventType">The outcome event's type string.</param>
    /// <param name="eventDataJson">The outcome event's payload.</param>
    /// <param name="nextStep">The instance's next step.</param>
    /// <param name="nextStatus">The instance's next status.</param>
    /// <param name="at">The operation instant captured by the kernel boundary.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AdvanceAsync(
        WorkflowStepKey key,
        WorkflowEffect? effect,
        string resultJson,
        string eventType,
        string eventDataJson,
        string nextStep,
        WorkflowStatus nextStatus,
        DateTimeOffset at,
        CancellationToken ct = default);

    /// <summary>
    /// Parks the instance on a delegated step durably (status = <see cref="WorkflowStatus.Parked"/>) and
    /// appends a <c>"Parked"</c> event — one transaction. Survives a restart; the instance resumes on the
    /// performer's typed result via <see cref="AdvanceAsync"/>.
    /// </summary>
    /// <param name="instanceId">The instance to park.</param>
    /// <param name="step">The delegated step to park on.</param>
    /// <param name="reasonJson">The park-event payload (e.g. the FE-1 basis / the send-back reason).</param>
    /// <param name="iteration">
    /// The instance's iteration counter to persist with this park (ADR 0135 A0). For a forward park (the
    /// normal CP-park, decide → the approve human-task) this is the instance's UNCHANGED iteration; for a
    /// loop-back park (the invoice <c>send-back</c> re-entry) the dispatcher passes the BUMPED iteration so the
    /// re-entered step's next advance derives a distinct, crash-stable idempotency key. Persisted atomically
    /// with the park so a resume reads the counter back verbatim (never recomputed — bug-1337 class). Defaults
    /// to 0 (a fresh instance's forward park), so existing 3-argument call sites keep their iteration-0 behaviour.
    /// </param>
    /// <param name="at">The operation instant captured by the kernel boundary.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ParkAsync(
        string instanceId,
        string step,
        string reasonJson,
        DateTimeOffset at,
        int iteration = 0,
        CancellationToken ct = default);
}

/// <summary>
/// An opaque domain-effect enlistment staged atomically inside <see cref="IWorkflowStore.AdvanceAsync"/>.
/// The engine core hands the store a closure; the store invokes <see cref="StageAsync"/> with its
/// in-flight unit-of-work handle (an EF context in the recoverable impl) so the effect's rows commit in
/// the SAME transaction as the advance — the ADR-0126 shared-unit-of-work pattern.
/// </summary>
/// <remarks>
/// <b>Why a delegate, not an interface the effect implements.</b> The effect type (a posted JE) lives in
/// the financial cluster; the engine core must not reference it. A thin delegate over an
/// <c>object</c> unit-of-work handle (cast by the impl to its concrete context) keeps the seam
/// dependency-free while still letting the effect ride the advance transaction. The impl is the only code
/// that knows the concrete handle type, so the cast is local and safe.
/// </remarks>
public sealed class WorkflowEffect
{
    private readonly Func<object, CancellationToken, Task> _stage;

    /// <summary>Wraps a staging closure. <paramref name="stage"/> receives the impl's unit-of-work handle.</summary>
    public WorkflowEffect(
        Func<object, CancellationToken, Task> stage,
        bool commitsIndependently = false)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        CommitsIndependently = commitsIndependently;
    }

    /// <summary>
    /// True when the effect owns a separate, idempotent writer and therefore must complete before the
    /// workflow store opens its transaction (SQLite permits no nested writers).
    /// </summary>
    public bool CommitsIndependently { get; }

    /// <summary>
    /// Stages the effect's rows onto the in-flight <paramref name="unitOfWork"/> WITHOUT committing — the
    /// store's single commit finalizes everything atomically. Throwing here aborts the whole advance.
    /// </summary>
    public Task StageAsync(object unitOfWork, CancellationToken ct = default) => _stage(unitOfWork, ct);
}
