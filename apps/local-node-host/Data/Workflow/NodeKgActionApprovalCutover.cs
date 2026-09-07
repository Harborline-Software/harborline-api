using System.Text.Json;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Search.Generation;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The proposed-CP-action → human-CP-park coordinator (ADR 0135 KG-search Slice 2-actions, G-G4). It is the
/// ONLY bridge from a grounded <see cref="KgGenerationProposal"/> that carries a proposed CP
/// <see cref="KgProposedAction"/> to the ADR-0135 engine: it serializes the proposal's basis + the structured
/// action into the instance working state, instantiates a <c>kg-action-approval</c> Process, and drives its
/// <c>decide</c> trigger so it PARKS on the human-CP-gate. <b>It does NOT execute the action</b> — the only path
/// to execution is a human approve resuming the engine (the <see cref="GraphRagProposalHandler"/> routes approve
/// to the existing CP path "as the human").
/// </summary>
/// <remarks>
/// <para>
/// <b>G-G4 — the structural no-autonomous-action property lives in this seam's SHAPE.</b> This coordinator's
/// only "act on a proposal" verb is <see cref="ParkForApprovalAsync"/>, whose name says it: it PARKS. There is
/// no <c>ExecuteAsync(proposal)</c> / <c>ApplyAsync(proposal)</c> here — a proposal cannot reach execution
/// through this type except by becoming a parked human-task that a human later approves. The
/// <see cref="GroundedProposalService"/> (which produces the proposal) does not reference this coordinator at
/// all; the proposal it returns is inert. So there is no path <c>generate → act</c>; the only path is
/// <c>generate → park → (human approve) → execute</c>. This is arch-tested (no direct proposal→action path).
/// </para>
/// <para>
/// <b>Injection defense.</b> An injected grounding that yields a proposal proposing a MALICIOUS action arrives
/// here exactly like a benign one — it is PARKED to the human (with the basis, incl. the injected proposal text
/// + the taint label, rendered before the confirm). The human sees it and rejects; there is no autonomous
/// action even when the grounding is adversarial. The human is the gate against injection-driven actions.
/// </para>
/// <para>
/// <b>Taint propagation.</b> The proposal's <see cref="KgProposalTaint"/> is serialized into the instance state
/// and surfaced in the park basis; on approve it rides the execute path into the durable-layer audit (the
/// resulting Draft JE's source-reference traces back to the parked, taint-labeled proposal).
/// </para>
/// </remarks>
public sealed class NodeKgActionApprovalCutover
{
    private readonly NodeWorkflowInstantiationService _instantiation;
    private readonly IWorkflowTriggerDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    /// <summary>The taint label string the engine basis carries (mirrors <see cref="KgProposalTaint.UntrustedDerived"/>).</summary>
    public const string UntrustedDerivedTaint = "untrusted-derived";

    /// <summary>Construct over the engine instantiation surface + the trigger dispatcher (both node-resident).</summary>
    public NodeKgActionApprovalCutover(
        NodeWorkflowInstantiationService instantiation,
        IWorkflowTriggerDispatcher dispatcher,
        TimeProvider timeProvider)
    {
        _instantiation = instantiation ?? throw new ArgumentNullException(nameof(instantiation));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// PARKS a grounded proposed CP action to the human-CP-gate: serializes the proposal's basis + structured
    /// action into the instance state, instantiates the <c>kg-action-approval</c> Process, and drives its
    /// <c>decide</c> trigger so it PARKS on the approve human-task. Returns the parked Process result.
    /// <b>Executes NOTHING</b> — the action stays a proposal until a human approve. Idempotent on
    /// <paramref name="proposalId"/>.
    /// </summary>
    /// <param name="tenantId">The tenant the proposal + Process are homed to (ADR 0135 D3 single-owner).</param>
    /// <param name="proposalId">A stable id for this proposed action (the inbox / resume key).</param>
    /// <param name="proposal">
    /// The grounded proposal — MUST carry a <see cref="KgGenerationProposal.Action"/> (it is a PROPOSED CP
    /// ACTION). An inert-text Q&amp;A proposal (null action) is NOT parked — it renders to the user directly
    /// (the Slice 2-foundation path); passing one here is a caller error.
    /// </param>
    /// <param name="action">
    /// The structured action input the host CP executor consumes on approve (e.g. the JE lines). Carried in the
    /// instance state, re-read + re-validated by the execute context at approve-time (never trusted inbound).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<KgActionParkResult> ParkForApprovalAsync(
        TenantId tenantId,
        string proposalId,
        KgGenerationProposal proposal,
        KgActionExecutionInput action,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(proposalId);
        ArgumentNullException.ThrowIfNull(proposal);

        if (!proposal.IsProposedAction)
        {
            // A Q&A proposal has no CP action — it is NOT parked (it renders to the user directly). Refusing
            // here keeps the "two output classes" boundary honest: only a proposed CP action ever parks.
            throw new InvalidOperationException(
                "ParkForApprovalAsync requires a proposal carrying a proposed CP action (Action != null); an " +
                "inert-text Q&A proposal renders to the user directly and must not be parked.");
        }

        var proposed = proposal.Action!;
        var stateJson = SerializeState(proposal, proposed, action);
        var at = _timeProvider.GetUtcNow();

        var instanceId = await _instantiation
            .StartKgActionApprovalAsync(tenantId, proposalId, stateJson, at, ct)
            .ConfigureAwait(false);

        // Drive the decide trigger → ALWAYS PARK on the approve human-task (G-G4 — no auto-approve). A re-park
        // of an already-decided proposal replays the decide as a no-op and stays parked.
        var dispatch = await _dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, GraphRagProposalSteps.Decide, at),
            ct).ConfigureAwait(false);

        return new KgActionParkResult(instanceId, dispatch);
    }

    /// <summary>
    /// Actions a parked kg-action-approval task by resuming the engine via a human-action trigger. The
    /// dispatcher routes it to <see cref="GraphRagProposalHandler"/>: <c>approve</c> → execute via the EXISTING
    /// CP path EXACTLY ONCE; <c>reject</c> → nothing runs (terminal); <c>send-back</c> → round-trip back to
    /// <c>decide</c> (re-park). Idempotent on redelivery — a second approve of a Completed instance returns
    /// <see cref="WorkflowDispatchResult.Terminal"/> and executes nothing new.
    /// </summary>
    public async Task<WorkflowDispatchResult> ResumeAsync(
        string instanceId,
        string decision,
        string? note,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(decision);

        var at = _timeProvider.GetUtcNow();
        var payload = BuildHumanActionPayload(decision, note);
        var result = await _dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, GraphRagProposalSteps.Approve, at, payload),
            ct).ConfigureAwait(false);

        // send-back round-trip: the handler parked the instance back on `decide`. Re-drive `decide` so it
        // RE-PARKS on approve — landing the task back in the Inbox in one operator action (mirrors the
        // invoice cutover's send-back round-trip). approve / reject are terminal and do NOT round-trip.
        if (string.Equals(decision, "send-back", StringComparison.Ordinal)
            && result == WorkflowDispatchResult.Parked)
        {
            result = await _dispatcher.DispatchAsync(
                WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, GraphRagProposalSteps.Decide, at),
                ct).ConfigureAwait(false);
        }

        return result;
    }

    private static string SerializeState(
        KgGenerationProposal proposal, KgProposedAction proposed, KgActionExecutionInput action)
        => JsonSerializer.Serialize(new
        {
            proposalText = proposal.Text,
            actionKind = proposed.Kind,
            actionSummary = proposed.Summary,
            groundingRecordIds = proposal.GroundingRecordIds,
            groundedOnInferredEdge = action.GroundedOnInferredEdge,
            taint = TaintLabel(proposal.Taint),
            action = new
            {
                debitAccount = action.DebitAccount,
                creditAccount = action.CreditAccount,
                amount = action.Amount,
                memo = action.Memo,
            },
        });

    /// <summary>Maps the proposal's taint enum to the basis label string (always untrusted-derived for a generated proposal).</summary>
    public static string TaintLabel(KgProposalTaint taint) => taint switch
    {
        KgProposalTaint.UntrustedDerived => UntrustedDerivedTaint,
        _ => UntrustedDerivedTaint,
    };

    private static string BuildHumanActionPayload(string decision, string? note)
    {
        var safeDecision = JsonEncodedText.Encode(decision).ToString();
        if (string.IsNullOrEmpty(note))
        {
            return $"{{\"decision\":\"{safeDecision}\"}}";
        }
        var safeNote = JsonEncodedText.Encode(note).ToString();
        return $"{{\"decision\":\"{safeDecision}\",\"note\":\"{safeNote}\"}}";
    }
}

/// <summary>
/// The structured CP execution input the host executor consumes on approve for a v1 <c>draft-journal-entry</c>
/// action. Distinct from the inert <see cref="KgProposedAction"/> (which is the model's description) — this is
/// the host-resolved, re-validated input the cutover stamps into the durable state. The execute context
/// re-reads it from the instance state, never from an inbound argument (the proposal is untrusted).
/// </summary>
/// <param name="DebitAccount">The GL account debited by the proposed Draft JE.</param>
/// <param name="CreditAccount">The GL account credited by the proposed Draft JE.</param>
/// <param name="Amount">The proposed Draft JE amount.</param>
/// <param name="Memo">The proposed Draft JE memo.</param>
/// <param name="GroundedOnInferredEdge">True iff the proposal cited an INFERRED (AI-hint) edge (§2.9; surfaced in the basis).</param>
public readonly record struct KgActionExecutionInput(
    string DebitAccount,
    string CreditAccount,
    decimal Amount,
    string Memo,
    bool GroundedOnInferredEdge);

/// <summary>
/// The result of parking a proposed CP action for approval — the parked Process instance id + how the
/// <c>decide</c> dispatch resolved (expected <see cref="WorkflowDispatchResult.Parked"/> — G-G4 never
/// auto-executes).
/// </summary>
/// <param name="InstanceId">The instantiated <c>kg-action-approval</c> Process id (the resume / inbox key).</param>
/// <param name="DecideResult">The dispatch outcome of the <c>decide</c> trigger (Parked on success).</param>
public readonly record struct KgActionParkResult(string InstanceId, WorkflowDispatchResult DecideResult);
