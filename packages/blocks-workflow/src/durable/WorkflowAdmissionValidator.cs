namespace Harborline.Api.Blocks.Workflow.Durable;

using Harborline.Api.Foundation.Scheduling;

// ─────────────────────────────────────────────────────────────────────────────
//  WF-KEY — the WORKFLOW ADMISSION VALIDATOR (ADR 0140 / ADR 0135 A1 R-1, made
//  authoring-time). The security keystone of WF-KEY: a fail-closed, structural,
//  re-run-on-every-D7-re-pin gate that REFUSES to admit a WorkflowDefinition that
//  would let an autonomous trigger reach an unclassified or un-gated CP action.
//
//  This is the .NET load-time face of the gate the ADR 0135 Amendment 2026-06-24
//  named R-1 — the invariant that lets the declarative definition be authored/stored
//  WITHOUT dismantling the engine's CP-safety fence (the general A1 interpreter stays
//  deferred). The Harborline App builder (apps/carrier/src/workflows/builder/admission.ts) is
//  a faithful TS port that surfaces the SAME refusals at design time, in the canvas.
//
//  Structural, not semantic: it reasons over the state/transition/trigger/action graph,
//  never over runtime data. Two invariants are load-bearing and fail-closed:
//    (1) An action that is NOT explicitly CP or AP ⇒ refuse (never default-allow; this
//        also fail-closes an out-of-range enum value, matching the TS gate's
//        `!== 'CP' && !== 'AP'`).
//    (2) A CP action must be HUMAN-GATED AT THE POINT OF FIRING — it may fire ONLY on a
//        HumanAction transition, OR on entering a non-initial state whose EVERY incoming
//        transition is a HumanAction. (The invoice approve→post-JE pattern: the human
//        approve IS the gating transition the CP post fires on.) This is STRICTER than
//        "a human is somewhere upstream on the path": a single upstream approval does NOT
//        license a later AUTONOMOUS (Schedule/Event/DependencyComplete) tick — nor an
//        autonomous back-edge re-entry — to fire the CP without a human gating that firing.
//  Plus reachability + referential integrity (mirrors the form/0135 BFS-throw-at-load).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Stable, locale-independent admission-violation codes (a client localizes off these).</summary>
public static class WorkflowAdmissionCodes
{
    /// <summary>An action declared no CP/AP classification (fail-closed refusal).</summary>
    public const string ActionUnclassified = "workflow.admission.action_unclassified";

    /// <summary>
    /// An action's author-declared classification does NOT match the class DERIVED from the canonical
    /// capability→authority registry (ADR 0143 — a CP capability cannot be laundered to AP; an unknown
    /// capability derives to CP). Fail-closed refusal.
    /// </summary>
    public const string ClassificationMismatch = "workflow.admission.classification_mismatch";

    /// <summary>A CP action is reachable from an autonomous trigger with no interposed human-task.</summary>
    public const string CpReachableWithoutHumanTask = "workflow.admission.cp_reachable_without_human_task";

    /// <summary>The declared initial state is not one of the definition's states.</summary>
    public const string InitialStateMissing = "workflow.admission.initial_state_missing";

    /// <summary>A reachable non-terminal state has no outgoing transition (a dead end).</summary>
    public const string DeadEndState = "workflow.admission.dead_end_state";

    /// <summary>A Terminal state has an outgoing transition (the Terminal contract is "no outgoing").</summary>
    public const string TerminalHasOutgoing = "workflow.admission.terminal_has_outgoing";

    /// <summary>A defined state is not reachable from the initial state.</summary>
    public const string UnreachableState = "workflow.admission.unreachable_state";

    /// <summary>A transition/action/initial references a state/trigger/guard that does not exist.</summary>
    public const string DanglingReference = "workflow.admission.dangling_reference";

    /// <summary>An action binding does not name exactly one of OnState / OnTransition.</summary>
    public const string ActionBindingInvalid = "workflow.admission.action_binding_invalid";

    /// <summary>Duplicate id within states / transitions / triggers / actions.</summary>
    public const string DuplicateId = "workflow.admission.duplicate_id";

    /// <summary>A Schedule trigger has no parseable RRULE and therefore cannot fire.</summary>
    public const string ScheduleRruleInvalid = "workflow.admission.schedule_rrule_invalid";
}

/// <summary>A single admission failure — a stable code + a human-readable message + the offending locator.</summary>
/// <param name="Code">A <see cref="WorkflowAdmissionCodes"/> value.</param>
/// <param name="Message">A human-readable English fallback (a localizing client keys off <paramref name="Code"/>).</param>
/// <param name="Locator">The offending id (state/transition/action id) for diagnostics; may be empty.</param>
public readonly record struct WorkflowAdmissionViolation(string Code, string Message, string Locator);

/// <summary>The outcome of admission — valid, or the surviving violations.</summary>
public sealed class WorkflowAdmissionResult
{
    /// <summary>True iff there are no violations.</summary>
    public bool IsValid => Violations.Count == 0;

    public required IReadOnlyList<WorkflowAdmissionViolation> Violations { get; init; }

    public static WorkflowAdmissionResult Valid { get; } =
        new() { Violations = Array.Empty<WorkflowAdmissionViolation>() };
}

/// <summary>Thrown by <see cref="IWorkflowAdmissionValidator.EnsureAdmissible"/> when a definition is refused.</summary>
public sealed class WorkflowAdmissionException(WorkflowAdmissionResult result)
    : InvalidOperationException(
        "WorkflowDefinition refused at admission: " +
        string.Join("; ", result.Violations.Select(v => $"[{v.Code}] {v.Message}")))
{
    public WorkflowAdmissionResult Result { get; } = result;
}

/// <summary>
/// The authoring/publish-time CP-reachability gate (ADR 0135 A1 R-1, made structural + fail-closed).
/// </summary>
public interface IWorkflowAdmissionValidator
{
    /// <summary>Validates a definition; returns the surviving violations (empty ⇒ admissible).</summary>
    WorkflowAdmissionResult Validate(WorkflowDefinition definition);

    /// <summary>Validates and THROWS <see cref="WorkflowAdmissionException"/> if not admissible (fail-closed).</summary>
    void EnsureAdmissible(WorkflowDefinition definition);
}

/// <summary>The default validator. Structural BFS — no runtime data, deterministic, idempotent.</summary>
/// <remarks>
/// The one non-structural input is the canonical capability→authority registry (ADR 0143): the
/// validator DERIVES each action's CP/AP class from it and treats the author-declared
/// <see cref="WorkflowActionBindingDef.Classification"/> as a non-authoritative assertion that must
/// MATCH the derived class. The registry is a deterministic, immutable lookup, so the validator stays
/// deterministic + idempotent (safe to re-run at admission AND at load — wf-key §3.6).
/// </remarks>
public sealed class WorkflowAdmissionValidator : IWorkflowAdmissionValidator
{
    private readonly ICapabilityAuthorityRegistry _registry;
    private readonly IRruleExpansionService _rrule;

    /// <summary>Constructs the validator over the CANONICAL capability→authority registry (ADR 0143).</summary>
    public WorkflowAdmissionValidator()
        : this(CapabilityAuthorityRegistry.Canonical, new InMemoryRruleExpansionService())
    {
    }

    /// <summary>
    /// Constructs the validator over an explicit capability→authority registry (test/DI seam). The
    /// registry is the source of truth for an action's CP/AP class; the authored label must match it.
    /// </summary>
    public WorkflowAdmissionValidator(ICapabilityAuthorityRegistry registry)
        : this(registry, new InMemoryRruleExpansionService())
    {
    }

    /// <summary>Constructs the validator over explicit capability and RRULE authorities.</summary>
    public WorkflowAdmissionValidator(ICapabilityAuthorityRegistry registry, IRruleExpansionService rrule)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _rrule = rrule ?? throw new ArgumentNullException(nameof(rrule));
    }

    public void EnsureAdmissible(WorkflowDefinition definition)
    {
        var result = Validate(definition);
        if (!result.IsValid)
            throw new WorkflowAdmissionException(result);
    }

    public WorkflowAdmissionResult Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var v = new List<WorkflowAdmissionViolation>();

        // ── Indices + duplicate-id detection ─────────────────────────────────
        var stateIds = BuildIdSet(definition.States.Select(s => s.Id), "state", v);
        var triggerById = BuildTriggerIndex(definition.Triggers, v);
        var transitionById = BuildIdMap(definition.Transitions.Select(t => (t.Id, t)), "transition", v);
        _ = BuildIdSet(definition.Actions.Select(a => a.Id), "action", v);
        var guardIds = new HashSet<string>(definition.GuardRuleIds);

        foreach (var trigger in definition.Triggers)
        {
            if (trigger.Kind == WorkflowTriggerKind.Schedule && !IsParseableRrule(trigger.Rrule))
            {
                v.Add(new(WorkflowAdmissionCodes.ScheduleRruleInvalid,
                    $"schedule trigger '{trigger.Id}' must declare a parseable RRULE.", trigger.Id));
            }
        }

        // ── Referential integrity ────────────────────────────────────────────
        if (!stateIds.Contains(definition.InitialState))
            v.Add(new(WorkflowAdmissionCodes.InitialStateMissing,
                $"initialState '{definition.InitialState}' is not a defined state.", definition.InitialState));

        foreach (var t in definition.Transitions)
        {
            if (!stateIds.Contains(t.From))
                v.Add(Dangling($"transition '{t.Id}' from-state '{t.From}'", t.Id));
            if (!stateIds.Contains(t.To))
                v.Add(Dangling($"transition '{t.Id}' to-state '{t.To}'", t.Id));
            if (!triggerById.ContainsKey(t.On))
                v.Add(Dangling($"transition '{t.Id}' trigger '{t.On}'", t.Id));
            if (t.Guard is { Length: > 0 } g && !guardIds.Contains(g))
                v.Add(Dangling($"transition '{t.Id}' guard '{g}'", t.Id));
        }

        foreach (var a in definition.Actions)
        {
            bool hasState = a.OnState is { Length: > 0 };
            bool hasTransition = a.OnTransition is { Length: > 0 };
            if (hasState == hasTransition) // neither or both
                v.Add(new(WorkflowAdmissionCodes.ActionBindingInvalid,
                    $"action '{a.Id}' must name exactly one of OnState / OnTransition.", a.Id));
            if (hasState && !stateIds.Contains(a.OnState!))
                v.Add(Dangling($"action '{a.Id}' OnState '{a.OnState}'", a.Id));
            if (hasTransition && !transitionById.ContainsKey(a.OnTransition!))
                v.Add(Dangling($"action '{a.Id}' OnTransition '{a.OnTransition}'", a.Id));
            if (a.Condition is { Length: > 0 } c && !guardIds.Contains(c))
                v.Add(Dangling($"action '{a.Id}' condition '{c}'", a.Id));
        }

        // ── Reachability (only meaningful once the initial state is valid) ────
        if (stateIds.Contains(definition.InitialState))
        {
            var reachable = ReachableStates(definition);

            foreach (var s in definition.States)
            {
                if (!reachable.Contains(s.Id))
                {
                    v.Add(new(WorkflowAdmissionCodes.UnreachableState,
                        $"state '{s.Id}' is not reachable from the initial state '{definition.InitialState}'.", s.Id));
                    continue;
                }
                bool isTerminal = s.Kind == WorkflowStateKind.Terminal;
                bool hasOutgoing = definition.Transitions.Any(t => t.From == s.Id);
                if (!isTerminal && !hasOutgoing)
                    v.Add(new(WorkflowAdmissionCodes.DeadEndState,
                        $"state '{s.Id}' is reachable but is neither Terminal nor has any outgoing transition.", s.Id));
                if (isTerminal && hasOutgoing)
                    v.Add(new(WorkflowAdmissionCodes.TerminalHasOutgoing,
                        $"state '{s.Id}' is Terminal but has an outgoing transition (Terminal ⇒ no outgoing).", s.Id));
            }

            // ── The two load-bearing security invariants ─────────────────────
            foreach (var a in definition.Actions)
            {
                // (1) Not explicitly CP/AP ⇒ refuse (fail-closed; never default-allow). This refuses both
                //     the Unspecified default AND any out-of-range enum value (e.g. a numeric-deserialized
                //     `99`), matching the TS gate's `!== 'CP' && !== 'AP'`.
                if (a.Classification != ActionClassification.CP && a.Classification != ActionClassification.AP)
                {
                    v.Add(new(WorkflowAdmissionCodes.ActionUnclassified,
                        $"action '{a.Id}' ({a.Kind} via '{a.CapabilityRef}') declares no CP/AP classification.", a.Id));
                    continue;
                }

                // (2) Registry-DERIVED classification (ADR 0143, red-team Chain 1+5 / F2). The class is
                //     DERIVED from the canonical capability→authority registry (unknown ⇒ CP, fail-closed) —
                //     the SAME source the harborline-sdk `authorityOf` + the `.mjs` bridge use. The authored
                //     label is a NON-authoritative assertion that MUST equal the derived class: a mismatch is
                //     refused (a CP capability cannot be laundered to AP), and an unknown capability derives
                //     to CP so it cannot be smuggled past the human-gate as AP. The human-gate fence below
                //     keys off the DERIVED class, never the authored label.
                var derived = _registry.AuthorityOf(a.CapabilityRef);
                if (a.Classification != derived)
                {
                    v.Add(new(WorkflowAdmissionCodes.ClassificationMismatch,
                        $"action '{a.Id}' declares '{a.Classification}' but capability '{a.CapabilityRef}' " +
                        $"resolves to '{derived}' in the authority registry — the declared class MUST match " +
                        $"the derived class (an unknown capability derives to CP).", a.Id));
                    continue;
                }

                // (3) A CP action (by the DERIVED class) that is NOT human-gated at the point of firing ⇒ refuse.
                if (derived == ActionClassification.CP &&
                    FiresWithoutHumanGate(a, definition, transitionById, triggerById))
                {
                    v.Add(new(WorkflowAdmissionCodes.CpReachableWithoutHumanTask,
                        $"CP action '{a.Id}' ({a.Kind} via '{a.CapabilityRef}') can fire without a human-task " +
                        $"gating the firing. A CP action may fire ONLY on a HumanAction transition, or on entering " +
                        $"a non-initial state whose every incoming transition is a HumanAction.", a.Id));
                }
            }
        }

        return v.Count == 0 ? WorkflowAdmissionResult.Valid : new WorkflowAdmissionResult { Violations = v };
    }

    private bool IsParseableRrule(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return false;
        }

        try
        {
            var anchor = new DateOnly(2000, 1, 1);
            _rrule.ExpandOccurrences(rrule, anchor, anchor, 0, 0, anchor, "UTC");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Does this CP action's fire-point fail the "park before firing" property — i.e. can it fire WITHOUT
    /// a HumanAction immediately gating the firing? (true ⇒ refuse.) This is a LOCAL structural property of
    /// the fire-point, NOT a reachability search — it is strictly stronger than "a human is somewhere
    /// upstream on the path", and it closes the two holes the taint model admitted (a CP on an autonomous
    /// edge downstream of one approval, and an autonomous back-edge re-firing an on-enter CP).
    ///   - OnTransition T ⇒ SAFE only if T's OWN trigger is a HumanAction (the human IS the gate — the
    ///     approve→post-JE pattern). Any autonomous (Event/Schedule/DependencyComplete) — or unknown/dangling,
    ///     fail-closed — trigger fires the CP without a human gating THIS firing ⇒ unsafe.
    ///   - OnState S ⇒ SAFE only if S is NOT the initial state AND S has ≥1 incoming transition AND EVERY
    ///     incoming transition is a HumanAction (so the only way to ENTER S — and thus fire the on-enter CP —
    ///     is a human approval). The initial state is entered autonomously on instantiation; a state with any
    ///     autonomous incoming edge (including an autonomous back-edge) can be re-entered and re-fire the CP
    ///     with no further human ⇒ unsafe.
    /// </summary>
    private static bool FiresWithoutHumanGate(
        WorkflowActionBindingDef action,
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, WorkflowTransitionDef> transitionById,
        IReadOnlyDictionary<string, WorkflowTriggerKind> triggerById)
    {
        if (action.OnTransition is { Length: > 0 } transitionId &&
            transitionById.TryGetValue(transitionId, out var t))
            // SAFE iff the firing transition itself is a HumanAction; otherwise it fires autonomously.
            return !IsHumanAction(t.On, triggerById);

        if (action.OnState is { Length: > 0 } stateId)
        {
            // The initial state is entered autonomously on instantiation ⇒ unsafe.
            if (stateId == definition.InitialState)
                return true;

            // SAFE iff there is ≥1 incoming edge AND every incoming edge is a HumanAction (the only way to
            // enter — and fire the on-enter CP — is a human approval). No incoming ⇒ no human-gated entry
            // exists (also unreachable ⇒ separately refused) ⇒ unsafe.
            var incoming = definition.Transitions.Where(tr => tr.To == stateId).ToList();
            return incoming.Count == 0 || incoming.Any(tr => !IsHumanAction(tr.On, triggerById));
        }

        // A malformed binding (already flagged by ActionBindingInvalid / DanglingReference) — treat as not
        // a CP-fence violation here; the definition is already refused.
        return false;
    }

    /// <summary>A trigger id resolves to a HumanAction (fail-closed: an unknown/dangling id is NOT human).</summary>
    private static bool IsHumanAction(
        string triggerId, IReadOnlyDictionary<string, WorkflowTriggerKind> triggerById)
        => triggerById.TryGetValue(triggerId, out var kind) && kind == WorkflowTriggerKind.HumanAction;

    /// <summary>
    /// BFS over the state graph from the initial state, traversing ALL transitions. The initial state is
    /// always included (a process can be instantiated and an autonomous trigger can fire its first step).
    /// </summary>
    private static HashSet<string> ReachableStates(WorkflowDefinition def)
    {
        var visited = new HashSet<string> { def.InitialState };
        var queue = new Queue<string>();
        queue.Enqueue(def.InitialState);

        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            foreach (var t in def.Transitions.Where(t => t.From == state))
                if (visited.Add(t.To))
                    queue.Enqueue(t.To);
        }
        return visited;
    }

    // ── small index helpers (with duplicate-id detection) ────────────────────

    private static HashSet<string> BuildIdSet(IEnumerable<string> ids, string kind, List<WorkflowAdmissionViolation> v)
    {
        var set = new HashSet<string>();
        foreach (var id in ids)
            if (!set.Add(id))
                v.Add(new(WorkflowAdmissionCodes.DuplicateId, $"duplicate {kind} id '{id}'.", id));
        return set;
    }

    private static Dictionary<string, T> BuildIdMap<T>(
        IEnumerable<(string Id, T Item)> items, string kind, List<WorkflowAdmissionViolation> v)
    {
        var map = new Dictionary<string, T>();
        foreach (var (id, item) in items)
            if (!map.TryAdd(id, item))
                v.Add(new(WorkflowAdmissionCodes.DuplicateId, $"duplicate {kind} id '{id}'.", id));
        return map;
    }

    private static Dictionary<string, WorkflowTriggerKind> BuildTriggerIndex(
        IEnumerable<WorkflowTriggerBindingDef> triggers, List<WorkflowAdmissionViolation> v)
    {
        var map = new Dictionary<string, WorkflowTriggerKind>();
        foreach (var t in triggers)
            if (!map.TryAdd(t.Id, t.Kind))
                v.Add(new(WorkflowAdmissionCodes.DuplicateId, $"duplicate trigger id '{t.Id}'.", t.Id));
        return map;
    }

    private static WorkflowAdmissionViolation Dangling(string what, string locator)
        => new(WorkflowAdmissionCodes.DanglingReference, $"{what} references something that does not exist.", locator);
}
