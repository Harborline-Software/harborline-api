using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The recoverable EF/SQLite <see cref="IWorkflowStore"/> for the embedded local node (ADR 0135 D2 — the
/// durable process engine's store impl). Rides the SAME <see cref="LocalNodeDbContext"/> the financial
/// writes use, so a step's effect (a posted JE) and the workflow advance (event + idempotency + position)
/// co-commit in ONE SQLite transaction — <b>build invariant #1</b> (atomic advance), the same ADR-0126
/// shared-unit-of-work property <see cref="Financial.NodeEfJournalStore"/> uses for its atomic audit row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The advance opens ONE explicit transaction on ONE context.</b> The optional
/// <see cref="WorkflowEffect"/> is handed THIS context (cast from the seam's <c>object</c> handle) and
/// stages its rows without committing; the event + idempotency + instance-position writes stage onto the
/// same context; a single <c>SaveChangesAsync</c> + <c>CommitAsync</c> finalizes all of it. If the effect
/// throws while staging — or the commit fails — the transaction is disposed un-committed and NOTHING
/// lands (the effect rolls back with everything else), so a crash in the window leaves the store exactly
/// as before and the resume finds no idempotency row → re-runs cleanly with no double-effect (ADR 0135 SC1).
/// </para>
/// <para>
/// <b>Mirrors the explicit-transaction shape of the de-risk spike</b> (which proved SC1 on the real
/// stack), promoted onto the production <see cref="LocalNodeDbContext"/>. EF would wrap a single
/// <c>SaveChanges</c> in an implicit transaction anyway; the EXPLICIT transaction is what lets the effect
/// stage onto the same connection and roll back atomically with the advance.
/// </para>
/// <para>
/// <b>Per-instance Seq allocation.</b> The next event <c>Seq</c> is read from the same context (max
/// existing Seq + 1) inside the transaction. SQLite serialises writes and the node is single-owner
/// (ADR 0135 D3 — the home is the only advancer), so there is no concurrent-Seq race on a single device;
/// the <c>ux_workflow_events_instance_seq</c> unique index is the durable backstop regardless.
/// </para>
/// </remarks>
public sealed class NodeEfWorkflowStore : IWorkflowStore
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the recoverable <c>local-node.db</c> context factory + the node clock.</summary>
    public NodeEfWorkflowStore(IDbContextFactory<LocalNodeDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task<WorkflowInstanceRecord?> LoadAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateInstanceAsync(
        WorkflowInstanceRecord instance,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.CreatedAt = at;
        instance.UpdatedAt = at;

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ctx.Set<WorkflowInstanceRecord>().Add(instance);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkflowStepIdempotencyRecord?> FindStepResultAsync(
        WorkflowStepKey key,
        CancellationToken ct = default)
    {
        var keyValue = key.Value;
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ctx.Set<WorkflowStepIdempotencyRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == keyValue, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AdvanceAsync(
        WorkflowStepKey key,
        WorkflowEffect? effect,
        string resultJson,
        string eventType,
        string eventDataJson,
        string nextStep,
        WorkflowStatus nextStatus,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        ArgumentException.ThrowIfNullOrEmpty(nextStep);

        if (effect?.CommitsIndependently == true)
        {
            await effect.StageAsync(null!, ct).ConfigureAwait(false);
            effect = null;
        }

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ONE explicit transaction spanning every write — the effect, the event, the idempotency row, and
        // the instance position. The explicit transaction (vs EF's implicit single-SaveChanges one) is what
        // lets the effect stage onto the SAME connection and roll back atomically with the advance.
        await using var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var instance = await ctx.Set<WorkflowInstanceRecord>()
            .FirstOrDefaultAsync(i => i.Id == key.InstanceId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Workflow instance '{key.InstanceId}' not found during advance for step '{key.Step}'.");

        // (1) DOMAIN EFFECT — staged onto THIS context (the seam hands the closure the context as its
        // unit-of-work handle). Throwing here aborts the whole advance: the transaction disposes
        // un-committed and nothing — including the effect — lands.
        if (effect is not null)
        {
            await effect.StageAsync(ctx, ct).ConfigureAwait(false);
        }

        // (2) OUTCOME EVENT — append-only, next per-instance Seq (read inside the transaction).
        var nextSeq = await NextSeqAsync(ctx, key.InstanceId, ct).ConfigureAwait(false);
        ctx.Set<WorkflowEventRecord>().Add(new WorkflowEventRecord
        {
            InstanceId = key.InstanceId,
            Seq = nextSeq,
            Step = key.Step,
            EventType = eventType,
            DataJson = string.IsNullOrEmpty(eventDataJson) ? "{}" : eventDataJson,
            OccurredAt = at,
        });

        // (3) IDEMPOTENCY row — the durable "this step advanced" proof, PK-keyed on the stable key.
        ctx.Set<WorkflowStepIdempotencyRecord>().Add(new WorkflowStepIdempotencyRecord
        {
            Key = key.Value,
            InstanceId = key.InstanceId,
            Step = key.Step,
            Iteration = key.Iteration,
            ResultJson = string.IsNullOrEmpty(resultJson) ? "{}" : resultJson,
            CompletedAt = at,
        });

        // (4) INSTANCE position/status update.
        instance.CurrentStep = nextStep;
        instance.Status = nextStatus;
        instance.UpdatedAt = at;

        // ONE commit finalizes all four atomically. A failure anywhere above (incl. the effect throw or a
        // unique-index violation) leaves the un-committed transaction to dispose with nothing persisted.
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ParkAsync(
        string instanceId,
        string step,
        string reasonJson,
        DateTimeOffset at,
        int iteration = 0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(step);
        ArgumentOutOfRangeException.ThrowIfNegative(iteration);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var instance = await ctx.Set<WorkflowInstanceRecord>()
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow instance '{instanceId}' not found during park at step '{step}'.");

        instance.CurrentStep = step;
        instance.Status = WorkflowStatus.Parked;
        // Persist the iteration counter atomically with the park (ADR 0135 A0). For a forward CP-park this is
        // the instance's unchanged iteration; for a loop-back park (send-back) the dispatcher passes the
        // BUMPED iteration so the re-entered step's next advance derives a distinct, crash-stable key. Read
        // back verbatim on resume (LoadAsync) — never recomputed (bug-1337 class).
        instance.Iteration = iteration;
        instance.UpdatedAt = at;

        var nextSeq = await NextSeqAsync(ctx, instanceId, ct).ConfigureAwait(false);
        ctx.Set<WorkflowEventRecord>().Add(new WorkflowEventRecord
        {
            InstanceId = instanceId,
            Seq = nextSeq,
            Step = step,
            EventType = "Parked",
            DataJson = string.IsNullOrEmpty(reasonJson) ? "{}" : reasonJson,
            OccurredAt = at,
        });

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The next per-instance event sequence (max existing + 1, or 0 for the first event). Read inside the
    /// caller's transaction so it sees rows staged earlier in the same unit-of-work; SQLite serialises
    /// writes and the node is single-owner, so no concurrent-Seq race on a single device.
    /// </summary>
    private static async Task<long> NextSeqAsync(LocalNodeDbContext ctx, string instanceId, CancellationToken ct)
    {
        var hasAny = await ctx.Set<WorkflowEventRecord>()
            .AnyAsync(e => e.InstanceId == instanceId, ct)
            .ConfigureAwait(false);
        if (!hasAny)
        {
            return 0;
        }

        var maxSeq = await ctx.Set<WorkflowEventRecord>()
            .Where(e => e.InstanceId == instanceId)
            .MaxAsync(e => e.Seq, ct)
            .ConfigureAwait(false);
        return maxSeq + 1;
    }
}
