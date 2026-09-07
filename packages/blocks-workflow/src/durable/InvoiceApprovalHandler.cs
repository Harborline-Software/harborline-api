using System.Text.Json;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  Handler A (ADR 0135 v1) — invoice > $5k → approve → post.
//
//  The first of the two typed v1 handlers (rule-of-three: no general step-graph
//  executor — each named process gets a hand-audited handler). Implements
//  IWorkflowStepHandler; the engine core supplies the durable plumbing (idempotency
//  guard, atomic advance, park/resume) around it.
//
//  Flow (steps: decide → [approve] → post / rejected):
//    decide   — evaluate the PINNED decision table (D7) against the invoice amount:
//                 * under threshold (AutoApprove)   → advance to `post`, attaching the
//                   posting effect (auto-post, no human — an AP step).
//                 * over threshold  (RequireApproval) → PARK on the `approve` human-task,
//                   carrying the FE-1 basis payload (posting preview + the decision-table
//                   row/version that fired). The post is NEVER auto-reached over threshold.
//    approve  — on a human-action result:
//                 * approve   → advance to `post`, attaching the posting effect. This is the
//                   ONLY way the CP `post` step is reached over threshold — the human approval
//                   IS the CP-park gate (ADR 0135 §Prerequisites, the v1 human-task interim).
//                 * reject    → terminal advance to `rejected`, NO effect (no post).
//                 * send-back → park back on `decide` for a revised amount (round-trip).
//
//  CP-park invariant (arch-tested BY NAME, the v1 DoD gate): the over-threshold path
//  returns Park on a human-task — it can NOT auto-advance to `post`. Asserted by a
//  step-level arch-test that drives an over-threshold decide and checks the outcome is a
//  Park whose payload carries the basis (the posting preview + the fired row/version).
//
//  Financial-free: the handler reads the amount + builds the post effect + builds the
//  posting preview through IInvoiceApprovalContext, which the host implements over the
//  financial cluster. blocks-workflow takes NO financial dependency.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The step identifiers + definition key for the invoice-approval process. Public so the host wiring, the
/// schedule/event source, and the arch-tests reference the SAME names (no string drift).
/// </summary>
public static class InvoiceApprovalSteps
{
    /// <summary>The definition key the dispatcher matches an instance's <c>DefinitionKey</c> against.</summary>
    public const string DefinitionKey = "invoice-approval";

    /// <summary>Entry step — evaluates the threshold decision table.</summary>
    public const string Decide = "decide";

    /// <summary>The human-task step an over-threshold invoice parks on (the CP-park gate).</summary>
    public const string Approve = "approve";

    /// <summary>The CP post step — the balanced JE post. Reached over-threshold ONLY via human approval.</summary>
    public const string Post = "post";

    /// <summary>Terminal step for a rejected invoice — no JE is posted.</summary>
    public const string Rejected = "rejected";

    /// <summary>Terminal step after a successful post.</summary>
    public const string Posted = "posted";
}

/// <summary>
/// The host-supplied surface the <see cref="InvoiceApprovalHandler"/> uses to stay financial-free: it reads
/// the invoice amount for an instance, builds the posting <see cref="WorkflowEffect"/> (which stages a real
/// balanced JE onto the advance transaction), and renders the FE-1 posting preview. The host implements this
/// over the financial cluster (the node posting service + the JE effect).
/// </summary>
public interface IInvoiceApprovalContext
{
    /// <summary>The invoice amount the decision table is evaluated against (the instance's working state).</summary>
    decimal GetInvoiceAmount(WorkflowInstanceRecord instance);

    /// <summary>The instant the decision is evaluated as-of (business time) — D7 pins the table version as-of this.</summary>
    DateTimeOffset GetBusinessTime(WorkflowInstanceRecord instance);

    /// <summary>
    /// Builds the post-step <see cref="WorkflowEffect"/> for <paramref name="instance"/> at
    /// <paramref name="postStepKey"/> — the effect stages the balanced JE onto the advance's in-flight
    /// unit-of-work (the host derives a deterministic JE id from the step key, build invariant #2).
    /// </summary>
    WorkflowEffect BuildPostEffect(
        WorkflowInstanceRecord instance,
        WorkflowStepKey postStepKey,
        DateTimeOffset at,
        Harborline.Api.Foundation.Authorization.AuthorizationDecision? admittedDecision = null);

    /// <summary>
    /// Renders the human-readable posting preview for the FE-1 basis payload (e.g. the debit/credit lines the
    /// post will produce). Pure projection — no side effect. Carried into the parked human-task so the CP
    /// confirm surface can render the basis BEFORE the confirm control (FE-1, ADR 0135 §Prerequisites).
    /// </summary>
    string RenderPostingPreview(WorkflowInstanceRecord instance, decimal amount);
}

/// <summary>
/// Handler A — <c>invoice &gt; $5k → approve → post</c> (ADR 0135 v1). See the file header for the full flow +
/// the CP-park invariant.
/// </summary>
public sealed class InvoiceApprovalHandler : IWorkflowStepHandler
{
    private readonly ThresholdDecisionTable _table;
    private readonly IInvoiceApprovalContext _context;

    /// <summary>Constructs the handler over its decision table (D7) + the host financial surface.</summary>
    public InvoiceApprovalHandler(ThresholdDecisionTable table, IInvoiceApprovalContext context)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public string DefinitionKey => InvoiceApprovalSteps.DefinitionKey;

    /// <inheritdoc />
    public ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance,
        WorkflowTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return ValueTask.FromResult(trigger.Step switch
        {
            InvoiceApprovalSteps.Decide => DecideThreshold(instance, trigger),
            InvoiceApprovalSteps.Approve => ResolveHumanAction(instance, trigger),
            _ => throw new InvalidOperationException(
                $"{nameof(InvoiceApprovalHandler)} received a trigger for unknown step '{trigger.Step}' " +
                $"(instance '{instance.Id}')."),
        });
    }

    /// <summary>
    /// The <c>decide</c> step. Evaluates the PINNED decision-table version (D7) against the invoice amount and
    /// branches: AutoApprove → advance to <c>post</c> with the effect (AP, no human); RequireApproval → PARK
    /// on the <c>approve</c> human-task carrying the FE-1 basis (the CP-park gate). The post is NEVER
    /// auto-reached over threshold — that is the arch-tested-by-name invariant.
    /// </summary>
    private WorkflowStepOutcome DecideThreshold(WorkflowInstanceRecord instance, WorkflowTrigger trigger)
    {
        var amount = _context.GetInvoiceAmount(instance);

        // D7 — evaluate the version PINNED on the instance at instantiation, never the as-of-now version, so
        // a replay is deterministic. The instance's DefinitionVersion IS the pinned decision-table version.
        var decision = _table.EvaluatePinned(instance.DefinitionVersion, amount);

        if (decision.Decision == ApprovalDecision.AutoApprove)
        {
            // Under threshold — auto-post (an AP step, no human). The effect stages the JE atomically with
            // the advance. The post step's effect key uses the instance's CURRENT durable iteration (0 for a
            // straight-through auto-post; the bumped value if a send-back round-trip preceded it) so the
            // derived JE source-reference stays consistent with the dispatcher's advance key.
            var postKey = new WorkflowStepKey(instance.Id, instance.Iteration, InvoiceApprovalSteps.Post);
            var effect = _context.BuildPostEffect(instance, postKey, OperationAt(instance, trigger));
            return WorkflowStepOutcome.Complete(
                finalStep: InvoiceApprovalSteps.Posted,
                effect: effect,
                resultJson: SerializeDecision(decision, posted: true, autoApproved: true),
                eventDataJson: SerializeDecision(decision, posted: true, autoApproved: true));
        }

        // Over threshold — PARK on the human-task. CP-park: the post step is NOT reached here. The park
        // payload carries the FE-1 basis (the posting preview + the fired row/version), so the CP confirm
        // surface can render the basis BEFORE the confirm control.
        var basis = BuildBasisPayload(instance, amount, decision);
        return WorkflowStepOutcome.Park(InvoiceApprovalSteps.Approve, basis);
    }

    /// <summary>
    /// The <c>approve</c> step, resumed by a human-action result. approve → advance to the CP <c>post</c> step
    /// WITH the effect (the human approval is the only path to the CP post over threshold); reject → terminal
    /// <c>rejected</c>, NO effect; send-back → park back on <c>decide</c> for a revised amount.
    /// </summary>
    private WorkflowStepOutcome ResolveHumanAction(WorkflowInstanceRecord instance, WorkflowTrigger trigger)
    {
        var action = HumanApprovalHandlerBase.ReadHumanAction(trigger.PayloadJson, InvoiceApprovalSteps.Approve);

        switch (action)
        {
            case "approve":
            {
                var postKey = new WorkflowStepKey(instance.Id, instance.Iteration, InvoiceApprovalSteps.Post);
                var effect = _context.BuildPostEffect(
                    instance, postKey, OperationAt(instance, trigger), trigger.AdmittedDecision);
                return WorkflowStepOutcome.Complete(
                    finalStep: InvoiceApprovalSteps.Posted,
                    effect: effect,
                    resultJson: "{\"decision\":\"approved\",\"posted\":true}",
                    eventDataJson: "{\"decision\":\"approved\",\"posted\":true}");
            }

            case "reject":
                // Terminal — no post. A pure advance to the rejected end state (no effect).
                return WorkflowStepOutcome.Complete(
                    finalStep: InvoiceApprovalSteps.Rejected,
                    effect: null,
                    resultJson: "{\"decision\":\"rejected\",\"posted\":false}",
                    eventDataJson: "{\"decision\":\"rejected\",\"posted\":false}");

            case "send-back":
                // Round-trip — park back on decide; the operator revises the amount and re-submits. This is a
                // LOOP-BACK (it re-enters the earlier decide step), so the dispatcher bumps the instance's
                // durable iteration AND enforces the max-iterations cap: a pathological send-back loop
                // terminates by escalation at the cap instead of ping-ponging forever (ADR 0135 A0).
                return WorkflowStepOutcome.ParkLoopBack(InvoiceApprovalSteps.Decide, "{\"decision\":\"send-back\"}");

            default:
                throw new InvalidOperationException(
                    $"{nameof(InvoiceApprovalHandler)} received an unknown human action '{action}' for " +
                    $"instance '{instance.Id}' (expected approve / reject / send-back).");
        }
    }

    private static DateTimeOffset OperationAt(WorkflowInstanceRecord instance, WorkflowTrigger trigger) =>
        trigger.At;

    private string BuildBasisPayload(WorkflowInstanceRecord instance, decimal amount, ThresholdDecisionResult decision)
    {
        // FE-1 (ADR 0135 §Prerequisites, binding): the CP human-task MUST carry the proposal's basis — the
        // posting preview + the rule/decision-table row + version that fired — so the confirm surface renders
        // the basis BEFORE the confirm control. This is the engine-side basis payload; the Harborline App Ask-bar
        // Inbox is its v1 home.
        var payload = new
        {
            kind = "cp-approval-basis",
            step = InvoiceApprovalSteps.Approve,
            amount,
            postingPreview = _context.RenderPostingPreview(instance, amount),
            decision = new
            {
                version = decision.Version,   // D7 — the pinned decision-table version that fired.
                row = decision.RowId,         // the row that fired.
                outcome = decision.Decision.ToString(),
            },
            typedOutcomes = new[] { "approve", "reject", "send-back" },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string SerializeDecision(ThresholdDecisionResult decision, bool posted, bool autoApproved)
        => JsonSerializer.Serialize(new
        {
            posted,
            autoApproved,
            decision = new { version = decision.Version, row = decision.RowId, outcome = decision.Decision.ToString() },
            amount = decision.EvaluatedAmount,
        });
}
