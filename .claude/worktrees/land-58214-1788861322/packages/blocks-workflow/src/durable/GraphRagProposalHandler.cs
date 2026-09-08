using System.Text.Json;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  Handler C (ADR 0135 KG-search Slice 2-actions) — a proposed CP action from a
//  grounded GraphRAG proposal → human-CP-gate → execute-as-the-human.
//
//  The third named CP-park handler (rule-of-three: NO general step-graph executor —
//  each named CP process is a hand-audited handler). Implements IWorkflowStepHandler;
//  the engine core supplies the durable plumbing (idempotency guard, atomic advance,
//  park/resume) around it. It is the structural realization of G-G4: a proposed CP
//  action NEVER executes without a human approve — it PARKS, and only the human approve
//  resumes the engine to the execute step. The model PROPOSES, the human ACTS.
//
//  Flow (steps: decide → [approve] → execute / rejected):
//    decide   — ALWAYS PARK on the `approve` human-task, carrying the FE-1 basis (the
//               proposal text + the proposed action summary + the grounding-path basis
//               + the asserted/inferred provenance + the TAINT label). There is NO
//               auto-approve branch: a proposed CP action is CP, full stop — it always
//               parks (unlike the invoice handler, whose under-threshold case is an AP
//               auto-post). The execute step is NEVER reached from `decide`.
//    approve  — on a human-action result:
//                 * approve   → advance to `execute`, attaching the CP execution effect.
//                   This is the ONLY way the CP `execute` step is reached — the human
//                   approval IS the gate (ADR 0135 §Prerequisites, the v1 human-task
//                   interim). The effect runs the EXISTING CP path "as the human".
//                 * reject    → terminal advance to `rejected`, NO effect (nothing runs).
//                 * send-back → park back on `decide` (round-trip; the operator can
//                   re-review). A bounded loop-back (A0 cap applies).
//
//  G-G4 invariant (arch-tested BY NAME, the slice DoD gate): the `decide` step ALWAYS
//  returns Park on the approve human-task — there is NO path from decide to execute. The
//  only path to `execute` is the approve human-action. An INJECTED grounding that proposes
//  a malicious action lands here too: it PARKS to the human, who sees the basis (incl. the
//  injected text + the taint label) and rejects — there is no autonomous action even when
//  the grounding is adversarial.
//
//  Generation-free + financial-free: the handler reads the proposal/action basis + builds
//  the CP execution effect through IKgActionApprovalContext, which the host implements over
//  the KG-generation layer + the existing CP executor. blocks-workflow takes NO dependency
//  on the Generation layer or the financial cluster.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The step identifiers + definition key for the GraphRAG proposed-action approval process. Public so the host
/// wiring, the instantiation surface, and the arch-tests reference the SAME names (no string drift).
/// </summary>
public static class GraphRagProposalSteps
{
    /// <summary>The definition key the dispatcher matches an instance's <c>DefinitionKey</c> against.</summary>
    public const string DefinitionKey = "kg-action-approval";

    /// <summary>Entry step — ALWAYS parks the proposed CP action on the human-task (no auto-approve branch).</summary>
    public const string Decide = "decide";

    /// <summary>The human-task step a proposed CP action parks on (the CP-park gate — the ONLY exit).</summary>
    public const string Approve = "approve";

    /// <summary>The CP execute step — runs the existing CP path. Reached ONLY via human approval.</summary>
    public const string Execute = "execute";

    /// <summary>Terminal step for a rejected proposed action — nothing runs.</summary>
    public const string Rejected = "rejected";

    /// <summary>Terminal step after a successful execute.</summary>
    public const string Executed = "executed";
}

/// <summary>
/// The host-supplied surface the <see cref="GraphRagProposalHandler"/> uses to stay generation-free +
/// financial-free: it reads the proposed action's basis (for the FE-1 park payload) and builds the CP
/// execution <see cref="WorkflowEffect"/> that runs the EXISTING CP path on approve. The host implements this
/// over the KG-generation layer (the parked proposal + its action) + the existing CP executor (e.g. the
/// JE-draft path). The execution effect re-validates the proposed action against authority at approve-time —
/// the proposal carries NO authority; the approving human's CP path does.
/// </summary>
public interface IKgActionApprovalContext
{
    /// <summary>
    /// Renders the FE-1 basis payload for the parked human-task from the instance working state — the proposal
    /// text + the proposed action summary + the grounding-path basis (the cited record ids) + the
    /// asserted/inferred provenance + the TAINT label. Pure projection — NO side effect. Carried into the park
    /// so the CP confirm surface renders the basis (incl. the taint) BEFORE the confirm control (FE-1, ADR 0135
    /// §Prerequisites). The human reviews this — including any injected text it surfaces — before any execution.
    /// </summary>
    KgActionApprovalBasis BuildBasis(WorkflowInstanceRecord instance);

    /// <summary>
    /// Builds the execute-step <see cref="WorkflowEffect"/> for <paramref name="instance"/> at
    /// <paramref name="executeStepKey"/> — the effect runs the proposed action through the EXISTING CP path
    /// "as the human", staged onto the advance's in-flight unit-of-work (atomic with the advance). Reached ONLY
    /// from the approve human-action. The effect re-validates the action's authority + structure at this point;
    /// a malformed / unauthorized / unknown-kind action aborts the advance (nothing executes, the instance
    /// stays parked / fails the resume — never a partial action).
    /// </summary>
    WorkflowEffect BuildExecuteEffect(
        WorkflowInstanceRecord instance,
        WorkflowStepKey executeStepKey,
        DateTimeOffset admittedAt);
}

/// <summary>
/// The engine-side FE-1 basis for a parked proposed-action human-task — the human-readable proposal + the
/// proposed action + its taint + provenance. The host builds it from the parked proposal; the handler weaves it
/// into the park payload so the confirm surface renders it BEFORE the confirm control.
/// </summary>
/// <param name="ProposalText">The grounded proposal text (UNTRUSTED-derived; presentation only).</param>
/// <param name="ActionKind">The NAMED CP action kind being proposed (e.g. <c>draft-journal-entry</c>).</param>
/// <param name="ActionSummary">A short human-readable summary of the proposed action.</param>
/// <param name="GroundingRecordIds">The authorized record ids the proposal cited (the grounding-path basis).</param>
/// <param name="GroundedOnInferredEdge">
/// True iff any cited edge was INFERRED (an AI hint, §2.9 det/ai split) rather than ASSERTED (a fact). Surfaced
/// in the basis so the human can weigh whether the proposal rests on a non-authoritative hint.
/// </param>
/// <param name="Taint">The taint label the proposal carries (always <c>untrusted-derived</c> for a generated proposal).</param>
public readonly record struct KgActionApprovalBasis(
    string ProposalText,
    string ActionKind,
    string ActionSummary,
    IReadOnlyList<string> GroundingRecordIds,
    bool GroundedOnInferredEdge,
    string Taint);

/// <summary>
/// Handler C — a proposed CP action from a grounded GraphRAG proposal → human-CP-gate → execute-as-the-human
/// (ADR 0135 KG-search Slice 2-actions). See the file header for the full flow + the G-G4 invariant.
/// </summary>
public sealed class GraphRagProposalHandler : IWorkflowStepHandler
{
    private readonly IKgActionApprovalContext _context;

    /// <summary>Constructs the handler over the host KG-action surface (basis + CP execution effect).</summary>
    public GraphRagProposalHandler(IKgActionApprovalContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public string DefinitionKey => GraphRagProposalSteps.DefinitionKey;

    /// <inheritdoc />
    public ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance,
        WorkflowTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return ValueTask.FromResult(trigger.Step switch
        {
            GraphRagProposalSteps.Decide => ParkForApproval(instance),
            GraphRagProposalSteps.Approve => ResolveHumanAction(instance, trigger),
            _ => throw new InvalidOperationException(
                $"{nameof(GraphRagProposalHandler)} received a trigger for unknown step '{trigger.Step}' " +
                $"(instance '{instance.Id}')."),
        });
    }

    /// <summary>
    /// The <c>decide</c> step. A proposed CP action is CP, full stop — it ALWAYS parks on the <c>approve</c>
    /// human-task carrying the FE-1 basis (the proposal + the action + the grounding basis + provenance + the
    /// TAINT label). There is NO auto-approve branch and NO path to <c>execute</c> from here — that is the
    /// G-G4 arch-tested-by-name invariant. Even an injected/adversarial proposed action parks here; the human
    /// is the gate.
    /// </summary>
    private WorkflowStepOutcome ParkForApproval(WorkflowInstanceRecord instance)
    {
        var basis = BuildBasisPayload(instance);
        return WorkflowStepOutcome.Park(GraphRagProposalSteps.Approve, basis);
    }

    /// <summary>
    /// The <c>approve</c> step, resumed by a human-action result. approve → advance to the CP <c>execute</c>
    /// step WITH the effect (the human approval is the only path to execute); reject → terminal
    /// <c>rejected</c>, NO effect (nothing runs); send-back → park back on <c>decide</c> for re-review.
    /// </summary>
    private WorkflowStepOutcome ResolveHumanAction(WorkflowInstanceRecord instance, WorkflowTrigger trigger)
    {
        var action = HumanApprovalHandlerBase.ReadHumanAction(trigger.PayloadJson, GraphRagProposalSteps.Approve);

        switch (action)
        {
            case "approve":
            {
                // The ONLY path to execute. The effect runs the EXISTING CP path "as the human", staged
                // atomically onto the advance. Reached ONLY via the approve human-action.
                var executeKey = new WorkflowStepKey(
                    instance.Id, instance.Iteration, GraphRagProposalSteps.Execute);
                var effect = _context.BuildExecuteEffect(instance, executeKey, trigger.At);
                return WorkflowStepOutcome.Complete(
                    finalStep: GraphRagProposalSteps.Executed,
                    effect: effect,
                    resultJson: "{\"decision\":\"approved\",\"executed\":true}",
                    eventDataJson: "{\"decision\":\"approved\",\"executed\":true}");
            }

            case "reject":
                // Terminal — nothing runs. A pure advance to the rejected end state (no effect). This is the
                // injection-defense outcome: the human, having seen the (possibly injected) basis, rejects.
                return WorkflowStepOutcome.Complete(
                    finalStep: GraphRagProposalSteps.Rejected,
                    effect: null,
                    resultJson: "{\"decision\":\"rejected\",\"executed\":false}",
                    eventDataJson: "{\"decision\":\"rejected\",\"executed\":false}");

            case "send-back":
                // Round-trip — park back on decide for re-review. A LOOP-BACK (re-enters the earlier decide
                // step), so the dispatcher bumps the instance's durable iteration AND enforces the
                // max-iterations cap (ADR 0135 A0) — a pathological send-back loop terminates by escalation.
                return WorkflowStepOutcome.ParkLoopBack(
                    GraphRagProposalSteps.Decide, "{\"decision\":\"send-back\"}");

            default:
                throw new InvalidOperationException(
                    $"{nameof(GraphRagProposalHandler)} received an unknown human action '{action}' for " +
                    $"instance '{instance.Id}' (expected approve / reject / send-back).");
        }
    }

    private string BuildBasisPayload(WorkflowInstanceRecord instance)
    {
        // FE-1 (ADR 0135 §Prerequisites, binding): the CP human-task MUST carry the proposal's basis — the
        // proposal text + the proposed action + the grounding-path basis + the asserted/inferred provenance +
        // the TAINT label — so the confirm surface renders the basis BEFORE the confirm control. The taint
        // label is part of the basis so the human SEES that the proposal is untrusted-derived (it may carry
        // injected text) before deciding.
        var b = _context.BuildBasis(instance);
        var payload = new
        {
            kind = "cp-action-basis",
            step = GraphRagProposalSteps.Approve,
            proposalText = b.ProposalText,
            action = new
            {
                kind = b.ActionKind,
                summary = b.ActionSummary,
            },
            grounding = new
            {
                recordIds = b.GroundingRecordIds,
                groundedOnInferredEdge = b.GroundedOnInferredEdge,   // §2.9 — an AI hint is never authoritative.
            },
            taint = b.Taint,                                          // the human SEES the untrusted-derived label.
            typedOutcomes = new[] { "approve", "reject", "send-back" },
        };
        return JsonSerializer.Serialize(payload);
    }
}
