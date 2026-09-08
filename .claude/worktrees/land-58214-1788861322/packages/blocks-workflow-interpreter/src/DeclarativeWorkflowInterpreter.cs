using System.Text.Json;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Blocks.Workflow.Interpreter;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0135 A1 — the general declarative workflow interpreter (W-8 execution).
//
//  It is the FALLBACK IWorkflowStepHandler analog the dispatcher consults when no typed
//  handler matches an instance's DefinitionKey. Where InvoiceApprovalHandler hand-codes ONE
//  process, this executes ANY authored + admitted + persisted WorkflowDefinition:
//
//    1. LOAD the instance's PINNED definition through the re-validating execution store
//       (IWorkflowDefinitionExecutionStore.GetAdmittedAsync) — never the lenient authoring
//       face (SC2 F-2). A now-inadmissible definition (a capability reclassified to CP, an
//       edit that routed a CP edge around the human-task, storage tamper) throws at load and
//       NEVER executes. Then re-parse the same authored JSON into the lean model.
//    2. TRAVERSE from the instance's current state:
//         • autonomous (Event/Schedule/DependencyComplete) — walk the single autonomous
//           outgoing transition per hop until a park point, an effecting action, or a terminal;
//         • human-action (a parked human-task resume) — select the outgoing HumanAction
//           transition matching the decision verb (the `decision:<verb>` guard convention),
//           then apply it.
//    3. REACH EFFECTS ONLY THROUGH THE BROKER (SC2). An AP action → BuildAutonomousEffect;
//       a CP action → ConfirmAndBuildEffect on the human-action resume that gates its firing
//       (SoD-checked). The interpreter lives in a SEPARATE assembly and composes ONLY the
//       public IWorkflowEffectBroker / IWorkflowEffectCatalog — it cannot name the internal
//       effect-factory surface (compile-enforced containment). A CP effect request is
//       re-derived from the PINNED definition + the durable instance state, never from the
//       confirm payload (ADR 0143 F-3) — the payload supplies ONLY the human's decision verb.
//
//  v1 execution scope (honest — richer authoring is additive, admission already fences it):
//    • single autonomous outgoing per state (multi-way autonomous branching needs a guard
//      evaluator — a documented extension point; fail-closed if >1 autonomous edge);
//    • human decisions routed by the `decision:<verb>` guard sentinel; `send-back` is a
//      bounded loop-back (iteration bump + the engine's max-iterations cap);
//    • guard EXPRESSIONS (SPINE-1) are not evaluated here — the sentinel is the v1 selector.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The default <see cref="IDeclarativeWorkflowInterpreter"/> (ADR 0135 A1). Executes an authored + admitted +
/// persisted <see cref="WorkflowDefinition"/> through the engine, reaching every side-effect only through the
/// broker-PEP. Deterministic + stateless (all durable state is the instance + the store).
/// </summary>
public sealed class DeclarativeWorkflowInterpreter : IDeclarativeWorkflowInterpreter
{
    /// <summary>The guard sentinel prefix that binds a HumanAction transition to a decision verb (v1 selector).</summary>
    public const string DecisionGuardPrefix = "decision:";

    /// <summary>The reserved human-decision verb that re-enters an earlier step as a bounded loop-back.</summary>
    public const string SendBackVerb = "send-back";

    /// <summary>
    /// The <c>kind</c> discriminator the interpreter stamps on the FE-1 basis of a parked CP action (see
    /// <see cref="BuildBasis"/>). A confirm surface / read model keys on this to select interpreter-parked CP
    /// confirmations (distinct from a typed handler's own basis shape).
    /// </summary>
    public const string CpApprovalBasisKind = "cp-approval-basis";

    private readonly IWorkflowDefinitionExecutionStore _executionStore;
    private readonly IWorkflowEffectBroker _broker;
    private readonly IWorkflowEffectCatalog _catalog;
    private readonly IWorkflowConfirmationContext _confirmation;

    /// <summary>
    /// Constructs the interpreter over the re-validating execution store (the ONLY definition-load seam it may
    /// take — SC2 F-2), the broker-PEP, the effect catalog (metadata), and the server-side confirmation context.
    /// </summary>
    public DeclarativeWorkflowInterpreter(
        IWorkflowDefinitionExecutionStore executionStore,
        IWorkflowEffectBroker broker,
        IWorkflowEffectCatalog catalog,
        IWorkflowConfirmationContext confirmation)
    {
        _executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance,
        WorkflowTrigger trigger,
        WorkflowDispatchAuthority authority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // ── LOAD (SC2 F-2): the PINNED (D7) definition through the re-validating execution store. This
        //    re-admits the persisted authored JSON against the CURRENT registry — fail-closed: a now-
        //    inadmissible definition throws WorkflowAdmissionException here and never executes. ──
        var record = await _executionStore
            .GetAdmittedAsync(new DefinitionCoordinates(
                new TenantId(instance.TenantId),
                instance.DefinitionKey,
                instance.DefinitionVersion), ct)
            .ConfigureAwait(false);

        // Re-parse the SAME authored JSON the store just re-admitted into the executable lean model. (Parse
        // only — admission already passed above; the wire mapper is the one canonical parse shared with
        // admission, so the shape we execute is the shape we admitted.)
        var definition = WorkflowDefinitionWireMapper.ToModel(
            record.Authored, record.Tenant, record.Key, record.Version);

        var triggerKinds = definition.Triggers.ToDictionary(t => t.Id, t => t.Kind, StringComparer.Ordinal);

        return trigger.Kind == WorkflowTriggerKind.HumanAction
            ? await DecideHumanActionAsync(instance, definition, triggerKinds, trigger, authority, ct).ConfigureAwait(false)
            : DecideAutonomous(instance, definition, triggerKinds, trigger, authority);
    }

    // ── Autonomous path: walk single autonomous transitions until park / effect / terminal ──
    private WorkflowStepOutcome DecideAutonomous(
        WorkflowInstanceRecord instance,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, WorkflowTriggerKind> triggerKinds,
        WorkflowTrigger trigger,
        WorkflowDispatchAuthority authority)
    {
        var state = trigger.Step;

        // Bound the traversal by the state count — an autonomous cycle (admission checks reachability +
        // dead-ends, not autonomous cycles) is a fail-closed error rather than an infinite loop.
        for (var hops = 0; hops <= definition.States.Count; hops++)
        {
            var current = FindState(definition, state);

            // A park point: a state whose outgoing edges are ALL HumanAction (the only way forward is a human).
            // Arriving here autonomously ⇒ PARK, carrying the CP basis for the confirm surface (ADR 0135 FE-1).
            if (IsHumanTaskParkPoint(definition, triggerKinds, state))
            {
                return WorkflowStepOutcome.Park(state, BuildBasis(definition, triggerKinds, state));
            }

            // An effecting action bound to entering this state fires here (an AP effect autonomously; a CP
            // effect reaching here autonomously is an admission failure — fail-closed).
            var onEnter = FindEffectingAction(definition, onState: state, onTransition: null);
            if (onEnter is not null)
            {
                return ApplyEffect(
                    instance, onEnter, nextStep: state,
                    nextIsTerminal: current.Kind == WorkflowStateKind.Terminal,
                    isHumanConfirm: false, confirmer: default, authority: authority);
            }

            if (current.Kind == WorkflowStateKind.Terminal)
            {
                return WorkflowStepOutcome.Complete(state);
            }

            // Exactly-one autonomous outgoing transition (v1). A guarded multi-way autonomous branch needs a
            // guard evaluator (documented extension) — >1 candidate is fail-closed.
            var autonomous = definition.Transitions
                .Where(t => t.From == state && !IsHumanAction(triggerKinds, t.On))
                .ToList();
            if (autonomous.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Interpreter: state '{state}' (instance '{instance.Id}') has no autonomous outgoing " +
                    "transition and is not a human-task park point or terminal — the instance cannot advance.");
            }
            if (autonomous.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Interpreter: state '{state}' (instance '{instance.Id}') has {autonomous.Count} autonomous " +
                    "outgoing transitions; the v1 interpreter supports a single autonomous edge (a guarded " +
                    "multi-way branch requires a guard evaluator).");
            }

            var transition = autonomous[0];

            // An effect bound to the transition itself (vs on-enter of the destination).
            var onTransition = FindEffectingAction(definition, onState: null, onTransition: transition.Id);
            if (onTransition is not null)
            {
                return ApplyEffect(
                    instance, onTransition, nextStep: transition.To,
                    nextIsTerminal: FindState(definition, transition.To).Kind == WorkflowStateKind.Terminal,
                    isHumanConfirm: false, confirmer: default, authority: authority);
            }

            // No effect, no park, not terminal — hop to the destination and continue the autonomous walk.
            state = transition.To;
        }

        throw new InvalidOperationException(
            $"Interpreter: autonomous traversal from '{trigger.Step}' (instance '{instance.Id}') exceeded the " +
            "state-count bound — the definition has an autonomous cycle with no park/effect/terminal.");
    }

    // ── Human-action path: the resume of a parked human-task ──
    private async ValueTask<WorkflowStepOutcome> DecideHumanActionAsync(
        WorkflowInstanceRecord instance,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, WorkflowTriggerKind> triggerKinds,
        WorkflowTrigger trigger,
        WorkflowDispatchAuthority authority,
        CancellationToken ct)
    {
        var state = trigger.Step;

        // The ONLY thing taken from the payload is the human's decision verb (ADR 0143 F-3 — everything
        // effect-relevant is re-derived from the pinned definition + durable state below, never the payload).
        var verb = HumanApprovalHandlerBase.ReadHumanAction(trigger.PayloadJson, state);

        // Select the outgoing HumanAction transition bound to this verb via the `decision:<verb>` guard
        // sentinel. Fail-closed: an unrecognized verb selects nothing (never silently pick a transition).
        var selected = definition.Transitions
            .Where(t => t.From == state && IsHumanAction(triggerKinds, t.On) && MatchesDecision(t, verb))
            .ToList();
        if (selected.Count != 1)
        {
            throw new InvalidOperationException(
                $"Interpreter: human decision '{verb}' on state '{state}' (instance '{instance.Id}') matched " +
                $"{selected.Count} outgoing HumanAction transitions (expected exactly one, bound via a " +
                $"'{DecisionGuardPrefix}{verb}' guard).");
        }

        var transition = selected[0];

        // send-back → a bounded loop-back (the dispatcher bumps the iteration + enforces the cap).
        if (string.Equals(verb, SendBackVerb, StringComparison.Ordinal))
        {
            return WorkflowStepOutcome.ParkLoopBack(
                transition.To, $"{{\"decision\":\"{SendBackVerb}\",\"from\":\"{state}\"}}");
        }

        // A CP effect gated by THIS human transition ⇒ the human approval IS the confirm (SoD-checked).
        var effecting = FindEffectingAction(definition, onState: null, onTransition: transition.Id)
            ?? FindEffectingAction(definition, onState: transition.To, onTransition: null);
        if (effecting is not null)
        {
            var confirmer = await _confirmation.ResolveConfirmerAsync(ct).ConfigureAwait(false);
            return ApplyEffect(
                instance, effecting, nextStep: transition.To,
                nextIsTerminal: FindState(definition, transition.To).Kind == WorkflowStateKind.Terminal,
                isHumanConfirm: true, confirmer: confirmer, authority: authority);
        }

        // No effect (e.g. reject → a terminal state): record the human OVERRIDE of the pending CP proposal for
        // the D-INV-7 metric and advance/complete with no side effect.
        var pendingCapability = PendingCpCapability(definition, state) ?? string.Empty;
        var destState = FindState(definition, transition.To);
        if (destState.Kind == WorkflowStateKind.Terminal)
        {
            _broker.RecordOverride(
                new WorkflowEffectRequest(
                    pendingCapability, instance, new WorkflowStepKey(instance.Id, instance.Iteration, transition.To), "{}"),
                await _confirmation.ResolveConfirmerAsync(ct).ConfigureAwait(false),
                authority.WorkflowDecision);
            return WorkflowStepOutcome.Complete(transition.To);
        }

        return WorkflowStepOutcome.Advance(transition.To);
    }

    // ── Effect application (the ONLY path to a side-effect — through the broker) ──
    private WorkflowStepOutcome ApplyEffect(
        WorkflowInstanceRecord instance,
        WorkflowActionBindingDef action,
        string nextStep,
        bool nextIsTerminal,
        bool isHumanConfirm,
        WorkflowConfirmerIdentity confirmer,
        WorkflowDispatchAuthority authority)
    {
        // The effect's step key is derived from the DESTINATION step (not the parked/current step), so the
        // factory derives a deterministic, replay-stable effect id independent of the advance's idempotency key.
        var effectKey = new WorkflowStepKey(instance.Id, instance.Iteration, nextStep);

        // The effect request is re-derived from the pinned definition (the action + its capability) + the
        // durable instance (which carries the instance-specific working state) — never the confirm payload.
        var request = new WorkflowEffectRequest(
            action.CapabilityRef, instance, effectKey, "{}", EffectDecision: authority.EffectDecision);

        var classification = _broker.Classify(action.CapabilityRef);

        WorkflowEffect effect;
        if (classification == ActionClassification.CP)
        {
            if (!isHumanConfirm)
            {
                // A CP effect reached WITHOUT a human gating its firing — admission should have refused this
                // definition. Fail-closed rather than build a CP effect autonomously.
                throw new InvalidOperationException(
                    $"Interpreter: CP capability '{action.CapabilityRef}' (action '{action.Id}', instance " +
                    $"'{instance.Id}') was reached without a human-action confirm — refusing to build it " +
                    "autonomously (a definition that admits this should have been refused at admission).");
            }

            // The CP confirm — SoD-gated in the broker. The proposer is the autonomous engine (non-human);
            // the confirmer is the server-resolved human. The broker throws WorkflowSodViolationException on a
            // non-human confirmer or an engine self-confirm; nothing is built or recorded on refusal.
            effect = _broker.ConfirmAndBuildEffect(
                request, _confirmation.EngineProposer, confirmer, authority.WorkflowDecision);
        }
        else
        {
            // AP — autonomous, no human. The broker refuses if the capability actually classifies CP.
            effect = _broker.BuildAutonomousEffect(request, authority.WorkflowDecision);
        }

        // Complete if the destination is terminal, else advance; either way the effect co-commits with the
        // advance (build invariant #1).
        return nextIsTerminal
            ? WorkflowStepOutcome.Complete(nextStep, effect,
                resultJson: EffectResultJson(action, nextStep),
                eventDataJson: EffectResultJson(action, nextStep))
            : WorkflowStepOutcome.Advance(nextStep, effect,
                resultJson: EffectResultJson(action, nextStep),
                eventDataJson: EffectResultJson(action, nextStep));
    }

    private WorkflowActionBindingDef? FindEffectingActionForCp(WorkflowDefinition definition, WorkflowTransitionDef t)
        => FindEffectingAction(definition, onState: t.To, onTransition: t.Id)
           ?? FindEffectingAction(definition, onState: null, onTransition: t.Id);

    private string? PendingCpCapability(WorkflowDefinition definition, string parkState)
        => definition.Transitions
            .Where(t => t.From == parkState)
            .Select(t => FindEffectingActionForCp(definition, t))
            .FirstOrDefault(a => a is not null)
            ?.CapabilityRef;

    private static string EffectResultJson(WorkflowActionBindingDef action, string nextStep)
        => $"{{\"executed\":true,\"action\":\"{action.Id}\",\"capability\":\"{action.CapabilityRef}\",\"step\":\"{nextStep}\"}}";

    // ── Structural helpers ──

    private static WorkflowStateDef FindState(WorkflowDefinition definition, string stateId)
        => definition.States.FirstOrDefault(s => s.Id == stateId)
           ?? throw new InvalidOperationException(
               $"Interpreter: state '{stateId}' is not defined in '{definition.Key}' v{definition.Version}.");

    private static bool IsHumanAction(IReadOnlyDictionary<string, WorkflowTriggerKind> triggerKinds, string triggerId)
        => triggerKinds.TryGetValue(triggerId, out var kind) && kind == WorkflowTriggerKind.HumanAction;

    private static bool IsHumanTaskParkPoint(
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, WorkflowTriggerKind> triggerKinds,
        string state)
    {
        var outgoing = definition.Transitions.Where(t => t.From == state).ToList();
        return outgoing.Count > 0 && outgoing.All(t => IsHumanAction(triggerKinds, t.On));
    }

    private WorkflowActionBindingDef? FindEffectingAction(
        WorkflowDefinition definition, string? onState, string? onTransition)
        => definition.Actions.FirstOrDefault(a =>
            ((onState is not null && a.OnState == onState) || (onTransition is not null && a.OnTransition == onTransition))
            && _catalog.IsEffectingCapability(a.CapabilityRef));

    private static bool MatchesDecision(WorkflowTransitionDef transition, string verb)
        => transition.Guard is { Length: > 0 } g
           && string.Equals(g, DecisionGuardPrefix + verb, StringComparison.Ordinal);

    private string BuildBasis(
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, WorkflowTriggerKind> triggerKinds,
        string parkState)
    {
        // The FE-1 basis carried into the parked human-task: the pending CP capability + the decision verbs
        // available (derived from the outgoing HumanAction transitions' guards). The Harborline App confirm surface
        // renders it BEFORE the confirm control. Instance-specific preview values live on the instance state.
        var outgoing = definition.Transitions.Where(t => t.From == parkState).ToList();
        var verbs = outgoing
            .Select(t => t.Guard)
            .Where(g => g is { Length: > 0 } && g.StartsWith(DecisionGuardPrefix, StringComparison.Ordinal))
            .Select(g => g!.Substring(DecisionGuardPrefix.Length))
            .ToList();

        return JsonSerializer.Serialize(new
        {
            kind = CpApprovalBasisKind,
            step = parkState,
            capabilityRef = PendingCpCapability(definition, parkState),
            typedOutcomes = verbs,
        });
    }
}
