using System.Collections.Frozen;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// The default <see cref="IWorkflowTriggerDispatcher"/> — the engine core's orchestration of the four
/// triggers (ADR 0135 D1). Stateless apart from the handler registry; all durable state lives in the
/// <see cref="IWorkflowStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The advance sequence (the load-bearing path):</b>
/// <list type="number">
///   <item>Load the instance. Unknown → <see cref="WorkflowDispatchResult.UnknownInstance"/>; terminal →
///     <see cref="WorkflowDispatchResult.Terminal"/>.</item>
///   <item><b>Idempotency guard.</b> Compute the <see cref="WorkflowStepKey"/> for the trigger's step and
///     look it up. If present, the step already advanced — REPLAY (no-op) and return
///     <see cref="WorkflowDispatchResult.ReplayedNoOp"/>. This is what makes a redelivered trigger or a
///     post-crash re-fire safe (ADR 0135 §Idempotency).</item>
///   <item>Ask the definition's handler for a <see cref="WorkflowStepOutcome"/>.</item>
///   <item>Commit it: <see cref="WorkflowStepOutcomeKind.Park"/> → durable park; otherwise the
///     idempotency-guarded ATOMIC advance (effect + event + idempotency row + position in one
///     transaction — build invariant #1). A crash inside the advance rolls everything back, so the resume
///     finds NO idempotency row and re-runs cleanly with no double-effect (ADR 0135 SC1).</item>
/// </list>
/// </para>
/// <para>
/// <b>Trigger-kind agnostic by design.</b> All four triggers (event · schedule · human-action ·
/// dependency-complete) funnel through the SAME advance sequence — they differ only in WHO fires them
/// (a domain-event subscriber, the schedule daemon, a human-task resume route, a dependency-join), not in
/// how the instance advances. That single funnel IS ADR 0135 D1's "one engine, four triggers".
/// </para>
/// </remarks>
public sealed class WorkflowTriggerDispatcher : IWorkflowTriggerDispatcher
{
    /// <summary>
    /// The terminal step id an instance escalates to when a loop-back hits the max-iterations cap (ADR 0135
    /// A0). A reserved engine-level step — not a handler step — so the audited history names the cause
    /// uniformly across definitions.
    /// </summary>
    public const string EscalatedStep = "__escalated-max-iterations";

    private readonly IWorkflowStore _store;
    private readonly FrozenDictionary<string, IWorkflowStepHandler> _handlers;
    private readonly IDeclarativeWorkflowInterpreter? _interpreter;
    private readonly WorkflowEngineOptions _options;

    /// <summary>
    /// Constructs the dispatcher over the durable store and the registered step handlers (one per
    /// definition key). Duplicate definition keys throw — a definition has exactly one handler.
    /// </summary>
    /// <param name="store">The durable process store.</param>
    /// <param name="handlers">The registered step handlers (one per definition key).</param>
    /// <param name="options">
    /// Engine tuning (the max-iterations cap). When <see langword="null"/>,
    /// <see cref="WorkflowEngineOptions.Default"/> is used (cap = 50).
    /// </param>
    /// <param name="interpreter">
    /// The general declarative interpreter (ADR 0135 A1), consulted ONLY when no per-definition
    /// <see cref="IWorkflowStepHandler"/> matches an instance's definition key. When <see langword="null"/>
    /// (the pre-A1 posture) an unhandled definition throws, exactly as before. A registered typed handler
    /// always wins for its key, so the interpreter is purely additive.
    /// </param>
    public WorkflowTriggerDispatcher(
        IWorkflowStore store,
        IEnumerable<IWorkflowStepHandler> handlers,
        WorkflowEngineOptions? options = null,
        IDeclarativeWorkflowInterpreter? interpreter = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToFrozenDictionary(h => h.DefinitionKey, StringComparer.Ordinal);
        _interpreter = interpreter;
        _options = (options ?? WorkflowEngineOptions.Default).Validated();
    }

    /// <inheritdoc />
    public Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, CancellationToken ct = default) =>
        DispatchCoreAsync(trigger, authority: null, ct);

    /// <inheritdoc />
    public Task<WorkflowDispatchResult> DispatchAsync(
        WorkflowTrigger trigger,
        AuthorizationDecision originatingDecision,
        CancellationToken ct = default) =>
        DispatchCoreAsync(trigger, new WorkflowDispatchAuthority(originatingDecision), ct);

    /// <inheritdoc />
    public Task<WorkflowDispatchResult> DispatchAsync(
        WorkflowTrigger trigger,
        WorkflowDispatchAuthority authority,
        CancellationToken ct = default) =>
        DispatchCoreAsync(trigger, authority, ct);

    private async Task<WorkflowDispatchResult> DispatchCoreAsync(
        WorkflowTrigger trigger,
        WorkflowDispatchAuthority? authority,
        CancellationToken ct)
    {
        var operationAt = trigger.At;
        if (authority is not null)
        {
            operationAt = authority.WorkflowDecision.Request.At;
            trigger = trigger with { At = operationAt };
        }
        var instance = await _store.LoadAsync(trigger.InstanceId, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return WorkflowDispatchResult.UnknownInstance;
        }

        if (instance.Status is WorkflowStatus.Completed or WorkflowStatus.Failed)
        {
            return WorkflowDispatchResult.Terminal;
        }

        // ── IDEMPOTENCY GUARD ──
        // The step key is per (instance, iteration, step). The iteration is the instance's DURABLE counter,
        // READ BACK from the loaded instance — never recomputed (bug-1337 class). A step re-entered via a
        // bounded loop (the invoice send-back) carries a HIGHER iteration than its first pass, so it gets a
        // DISTINCT key and is not falsely deduped against the earlier pass. If the row already exists, this
        // exact (instance, iteration, step) advanced before: replay (no-op), never re-run the effect.
        var key = new WorkflowStepKey(trigger.InstanceId, instance.Iteration, trigger.Step);
        var recorded = await _store.FindStepResultAsync(key, ct).ConfigureAwait(false);
        if (recorded is not null)
        {
            return WorkflowDispatchResult.ReplayedNoOp;
        }

        // A registered per-definition handler always wins for its key (the hand-audited invoice / recurring /
        // kg handlers). The general A1 interpreter is the FALLBACK: consulted only when no typed handler
        // matches, it executes the instance's pinned, re-admitted declarative definition. With neither a
        // handler nor an interpreter, an unhandled definition throws (the pre-A1 posture — unchanged).
        WorkflowStepOutcome outcome;
        if (_handlers.TryGetValue(instance.DefinitionKey, out var handler))
        {
            var admittedTrigger = trigger with
            {
                AdmittedDecision = authority?.EffectDecision ?? authority?.WorkflowDecision,
            };
            outcome = await handler.DecideAsync(instance, admittedTrigger, ct).ConfigureAwait(false);
        }
        else if (_interpreter is not null)
        {
            if (authority is null)
                throw new InvalidOperationException("A declarative workflow reaction requires its originating authorization decision.");
            authority.WorkflowDecision.RequireAllowedReaction(
                Harborline.Api.Foundation.IdentityAtlas.Permissions.AuthorizationOperation.Parse(
                    Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.RecordsWrite),
                new Harborline.Api.Foundation.Assets.Common.TenantId(instance.TenantId),
                "record",
                instance.Id);
            outcome = await _interpreter.DecideAsync(instance, trigger, authority, ct).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"No {nameof(IWorkflowStepHandler)} registered for definition '{instance.DefinitionKey}' " +
                $"(instance '{trigger.InstanceId}') and no {nameof(IDeclarativeWorkflowInterpreter)} fallback " +
                "is configured.");
        }

        if (outcome.Kind == WorkflowStepOutcomeKind.Park)
        {
            return await CommitParkAsync(
                    trigger.InstanceId, instance.Iteration, outcome, key, operationAt, ct)
                .ConfigureAwait(false);
        }

        // ATOMIC ADVANCE — effect (if any) + outcome event + idempotency row + position, one transaction.
        await _store.AdvanceAsync(
            key: key,
            effect: outcome.Effect,
            resultJson: outcome.ResultJson,
            eventType: outcome.EventType,
            eventDataJson: outcome.EventDataJson,
            nextStep: outcome.NextStep,
            nextStatus: outcome.NextStatus,
            at: operationAt,
            ct: ct).ConfigureAwait(false);

        return WorkflowDispatchResult.Advanced;
    }

    /// <summary>
    /// Commits a park outcome. A FORWARD park (the normal CP-park, decide → the approve human-task) parks at
    /// the instance's UNCHANGED iteration. A LOOP-BACK park (the invoice <c>send-back</c> re-entry) BUMPS the
    /// durable iteration so the re-entered step gets a distinct idempotency key — AND enforces the
    /// max-iterations cap: when the bump would reach <see cref="WorkflowEngineOptions.MaxIterations"/>, the
    /// instance is ESCALATED to a terminal failed state via an atomic advance instead of re-parking, closing
    /// the shipped unbounded-send-back loop (ADR 0135 A0, bug-1353).
    /// </summary>
    private async Task<WorkflowDispatchResult> CommitParkAsync(
        string instanceId,
        int currentIteration,
        WorkflowStepOutcome outcome,
        WorkflowStepKey decidedKey,
        DateTimeOffset at,
        CancellationToken ct)
    {
        if (!outcome.IsLoopBack)
        {
            // Forward park — no re-entry, iteration unchanged.
            await _store.ParkAsync(
                    instanceId, outcome.NextStep, outcome.EventDataJson, at, currentIteration, ct)
                .ConfigureAwait(false);
            return WorkflowDispatchResult.Parked;
        }

        // Loop-back park — this re-enters an earlier step. Bump the durable iteration so the re-entered step
        // derives a distinct, crash-stable idempotency key on its next pass.
        var nextIteration = currentIteration + 1;

        if (nextIteration >= _options.MaxIterations)
        {
            // CAP HIT — escalate to a terminal failed state rather than re-parking into an unbounded loop.
            // The escalation rides the engine's atomic advance, keyed at the CURRENT iteration (the key the
            // handler decided against), so the escalation itself is idempotent on redelivery.
            var escalationJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "max-iterations-escalation",
                reason = "loop-back iteration cap reached",
                step = outcome.NextStep,
                iteration = currentIteration,
                maxIterations = _options.MaxIterations,
            });
            await _store.AdvanceAsync(
                key: decidedKey,
                effect: null,
                resultJson: escalationJson,
                eventType: "Escalated",
                eventDataJson: escalationJson,
                nextStep: EscalatedStep,
                nextStatus: WorkflowStatus.Failed,
                at: at,
                ct: ct).ConfigureAwait(false);
            return WorkflowDispatchResult.Advanced;
        }

        // Under the cap — re-park at the bumped iteration (persisted atomically with the park).
        await _store.ParkAsync(
            instanceId, outcome.NextStep, outcome.EventDataJson, at, nextIteration, ct)
            .ConfigureAwait(false);
        return WorkflowDispatchResult.Parked;
    }
}
