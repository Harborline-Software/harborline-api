using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The invoice-issue → process-engine CUTOVER (ADR 0135 Handler A, the vertical-flow backend). Decides, for
/// a Draft invoice about to be issued, whether the issue goes <b>direct</b> (≤ threshold — post the JE
/// inline, unchanged) or is <b>routed through the engine</b> (&gt; threshold — create an
/// <c>invoice-approval</c> Process that PARKS the approval; the JE posts only on approve).
/// </summary>
/// <remarks>
/// <para>
/// <b>The #1348 deep-review's no-double-post property lives here.</b> For the over-threshold case this
/// instantiates the approval Process and drives its <c>decide</c> trigger (→ park on the approve human-task)
/// — and the invoice-issue route, seeing <see cref="ShouldRouteToEngine"/>, returns WITHOUT calling
/// <c>IInvoicePostingService.IssueAsync</c>. So the over-threshold invoice has NO inline issuer; its only
/// issuer is the approval Process's post effect (<see cref="NodeLiveInvoiceApprovalContext.BuildPostEffect"/>),
/// reached only via the approve human-action. There are not two issuers — there is one, behind the human gate.
/// The threshold lives in ONE place (<see cref="NodeWorkflowDefinitions.V1ApprovalThreshold"/>) so the route's
/// branch and the handler's pinned decision-table agree (no split-brain on the gate boundary).
/// </para>
/// <para>
/// <b>D7 pinning.</b> The Process pins <see cref="NodeWorkflowDefinitions.InvoiceApprovalV1Version"/> at
/// instantiation (the instantiation service stamps it), so the handler evaluates the SAME pinned decision
/// table the route branched on.
/// </para>
/// </remarks>
public sealed class NodeInvoiceApprovalCutover
{
    private readonly NodeWorkflowInstantiationService _instantiation;
    private readonly IWorkflowTriggerDispatcher _dispatcher;
    private readonly IWorkflowStore? _store;
    private readonly AuthorizationGate? _authorizationGate;
    private readonly SeparationOfDutyEngine? _separationOfDuty;
    private readonly IAuthorizedAuditTrail? _audit;
    private readonly IOperationSigner? _signer;
    private readonly IPeriodResolver? _periods;
    private readonly IInvoiceRepository? _invoices;

    /// <summary>
    /// The posting-period state recorded when the resolver finds NO period covering the journal entry's
    /// date. It is not <see cref="SeparationOfDutyEngine.OpenPostingPeriod"/>, so the engine refuses the
    /// approve — the same outcome the posting service would reach with <c>PostError.NoPeriodForDate</c>,
    /// taken before the entry rather than after it.
    /// </summary>
    public const string NoPeriodForDate = "NoPeriodForDate";

    /// <summary>The audit event type an invoice approval decision is recorded under.</summary>
    public static readonly AuditEventType ApprovalDecidedEventType = new("InvoiceApprovalDecided");

    /// <summary>
    /// Construct over the engine instantiation surface + the trigger dispatcher (both node-resident). The
    /// authorized-approve surface — the workflow store, the authorization gate, the separation-of-duty
    /// engine, the audit trail and the payload signer — is all-or-nothing: an approve that posts a journal
    /// entry is authorized, duty-checked and audited, or the cutover is not wired for authorized approves at
    /// all. Supplying only some of them would leave a path that posts without a recorded duty decision.
    /// </summary>
    public NodeInvoiceApprovalCutover(
        NodeWorkflowInstantiationService instantiation,
        IWorkflowTriggerDispatcher dispatcher,
        IWorkflowStore? store = null,
        AuthorizationGate? authorizationGate = null,
        SeparationOfDutyEngine? separationOfDuty = null,
        IAuthorizedAuditTrail? audit = null,
        IOperationSigner? signer = null,
        IPeriodResolver? periodResolver = null,
        IInvoiceRepository? invoices = null)
    {
        _instantiation = instantiation ?? throw new ArgumentNullException(nameof(instantiation));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        var wired = new object?[]
        {
            store, authorizationGate, separationOfDuty, audit, signer, periodResolver, invoices,
        };
        if (wired.Any(part => part is null) && wired.Any(part => part is not null))
            throw new ArgumentException(
                "The workflow store, authorization gate, separation-of-duty engine, audit trail, signer, "
                + "period resolver and invoice repository must be supplied together — an authorized approve "
                + "is duty-checked against a resolved posting period and audited, or it is absent.");
        _store = store;
        _authorizationGate = authorizationGate;
        _separationOfDuty = separationOfDuty;
        _audit = audit;
        _signer = signer;
        _periods = periodResolver;
        _invoices = invoices;
    }

    /// <summary>
    /// True iff issuing <paramref name="invoice"/> must go through the approval engine — i.e. its total is
    /// STRICTLY ABOVE the v1 threshold (the same strictly-above gate the pinned decision table uses, so
    /// <c>$5000.00</c> exactly stays direct-post). The route consults THIS, not its own copy of the number.
    /// </summary>
    public static bool ShouldRouteToEngine(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.Total > NodeWorkflowDefinitions.InvoiceApprovalThreshold.Limit;
    }

    /// <summary>
    /// Routes an over-threshold Draft <paramref name="invoice"/> through the engine: instantiates the
    /// <c>invoice-approval</c> Process (carrying the invoice's true total + AR / first-income accounts in the
    /// working state, D7-pinned) and drives its <c>decide</c> trigger so it PARKS on the approve human-task.
    /// Returns the parked Process result. <b>Posts NO JE</b> — the invoice stays Draft until approved.
    /// Idempotent on the invoice id (a re-issue of an already-routed invoice resolves the existing Process).
    /// </summary>
    /// <param name="tenantId">The tenant the invoice + Process are homed to (single-owner, ADR 0135 D3).</param>
    /// <param name="invoice">The Draft invoice being issued (must be over threshold — the caller checked).</param>
    /// <param name="requestedBy">
    /// The principal issuing the invoice. Recorded on the working state because it is the party a later
    /// approve is separated from: an approver who is this actor is self-approving (ADR 0067 clause 2).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<InvoiceApprovalRouteResult> RouteForApprovalAsync(
        TenantId tenantId,
        Invoice invoice,
        ActorId requestedBy,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        // Carry the invoice's TRUE total as the amount (the value the threshold rule + the FE-1 preview use),
        // and the AR + first-income accounts for the preview text. The post effect re-resolves the live
        // invoice by id and issues it through the real posting service, so the JE shape is the invoice's real
        // multi-line / tax entry — these accounts feed only the human-readable preview.
        var debitAccount = invoice.ArAccountId.Value;
        var creditAccount = invoice.Lines.Count > 0 ? invoice.Lines[0].IncomeAccountId.Value : "4000";
        var memo = string.Create(
            CultureInfo.InvariantCulture,
            $"Invoice {invoice.InvoiceNumber} ({invoice.CustomerId.Value})");

        var instanceId = await _instantiation.StartInvoiceApprovalAsync(
            tenantId:      tenantId,
            invoiceId:     invoice.Id.Value,
            amount:        invoice.Total,
            debitAccount:  debitAccount,
            creditAccount: creditAccount,
            memo:          memo,
            requestedBy:   requestedBy.Value,
            at:            at,
            ct:            ct).ConfigureAwait(false);

        // Drive the decide trigger → over-threshold → PARK on the approve human-task (no JE). A re-issue of an
        // already-decided invoice replays the decide as a no-op (idempotency guard) and stays parked.
        var dispatch = await _dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, InvoiceApprovalSteps.Decide, at),
            ct).ConfigureAwait(false);

        // The threshold travels back on the result: the route reports the number this call actually gated
        // on, rather than re-reading a constant at the point it renders the response.
        return new InvoiceApprovalRouteResult(
            instanceId, dispatch, NodeWorkflowDefinitions.InvoiceApprovalThreshold.Limit);
    }

    /// <summary>
    /// Actions a parked invoice-approval task by resuming the engine via a human-action trigger. The
    /// dispatcher routes it to <see cref="InvoiceApprovalHandler"/>: <c>approve</c> → post EXACTLY ONCE (the
    /// effect issues the real invoice); <c>reject</c> → no post (terminal); <c>send-back</c> → round-trip back
    /// to <c>decide</c>. Idempotent on redelivery — a second approve of a Completed instance returns
    /// <see cref="WorkflowDispatchResult.Terminal"/> and posts nothing new.
    /// </summary>
    /// <param name="instanceId">The parked Process instance id.</param>
    /// <param name="decision">One of <c>approve</c> / <c>reject</c> / <c>send-back</c>.</param>
    /// <param name="note">Optional operator note (carried into the human-action payload).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="authority">The request authority of the actor taking the action.</param>
    /// <param name="conflictOverride">
    /// An override of a declared duty conflict — the reason and the actor giving it. ADR 0067 clause 2 makes
    /// both mandatory, and the overrider may not be the requester; the engine, not this method, decides
    /// whether the override actually stands.
    /// </param>
    public async Task<InvoiceApprovalResumeResult> ResumeAsync(
        string instanceId,
        string decision,
        string? note,
        DateTimeOffset at,
        CancellationToken ct = default,
        AuthorizationWriteContext? authority = null,
        InvoiceApprovalOverride? conflictOverride = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(decision);

        SeparationOfDutyDecision? approvalDecision = null;
        var payload = BuildHumanActionPayload(decision, note);
        var trigger = WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, instanceId, InvoiceApprovalSteps.Approve, at, payload);
        WorkflowDispatchResult result;
        if (string.Equals(decision, "approve", StringComparison.Ordinal) && _authorizationGate is not null)
        {
            var writeAuthority = authority
                ?? throw new InvalidOperationException("An approval that posts a journal entry requires request authority.");
            var instance = await _store!.LoadAsync(instanceId, ct).ConfigureAwait(false);
            if (instance is null)
                return new InvoiceApprovalResumeResult(WorkflowDispatchResult.UnknownInstance, null);
            var postKey = new WorkflowStepKey(instance.Id, instance.Iteration, InvoiceApprovalSteps.Post);
            var journalId = NodeLiveInvoiceApprovalContext.JournalEntryIdFor(postKey);
            var gateRequest = writeAuthority.Request(
                AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost),
                "journal-entry",
                journalId.Value);
            var admittedDecision = await _authorizationGate.DecideAsync(gateRequest, ct).ConfigureAwait(false);
            admittedDecision.RequireAllowed();

            // ADR 0067 clause 2 — decided ONCE, here, from the threshold built once in
            // NodeWorkflowDefinitions; nothing downstream re-reads a threshold or re-decides the duty.
            var approval = _separationOfDuty!.Decide(new SeparationOfDutyRequest(
                Act:                admittedDecision.Request.Act,
                Principal:          RequestedBy(instance.StateJson),
                Tenant:             admittedDecision.Request.Tenant,
                ApproversOnRecord:  await ApproversOnRecordAsync(
                                        writeAuthority, gateRequest, conflictOverride, ct)
                                        .ConfigureAwait(false),
                Threshold:          NodeWorkflowDefinitions.InvoiceApprovalThreshold,
                // Ticket 272 slice 5: the fifth approval fact is READ from the period resolver — the same
                // IPeriodResolver the posting path gates on — for the very date and chart this approve's
                // journal entry will carry. Never a constant.
                PostingPeriodState: await ResolvePostingPeriodAsync(
                                        admittedDecision.Request.Tenant,
                                        instance.StateJson,
                                        admittedDecision.DecidedAt,
                                        ct).ConfigureAwait(false),
                At:                 admittedDecision.DecidedAt));

            // The decision is recorded BEFORE the act either way: a refusal is audited with the same
            // decision it was refused by, so a self-approval attempt is on the record, not merely absent.
            await RecordApprovalAsync(admittedDecision, approval, decision, ct).ConfigureAwait(false);

            // A refused approve dispatches NOTHING — no journal entry is posted and the task stays parked.
            if (!approval.Approved)
                return new InvoiceApprovalResumeResult(WorkflowDispatchResult.Parked, approval);

            result = await _dispatcher.DispatchAsync(
                trigger,
                new WorkflowDispatchAuthority(admittedDecision, admittedDecision),
                ct).ConfigureAwait(false);
            approvalDecision = approval;
        }
        else
        {
            result = await _dispatcher.DispatchAsync(trigger, ct).ConfigureAwait(false);
        }

        // send-back round-trip: the handler parked the instance back on `decide`. Re-drive the `decide`
        // trigger so the Process re-evaluates the (pinned) threshold and RE-PARKS on the approve human-task —
        // landing the task back in the Inbox in ONE operator action (the observable round-trip). The
        // re-decide is the engine re-running an automated step; it posts NO JE (still over threshold → park).
        // approve / reject do NOT round-trip (the instance is terminal), so this only fires for send-back.
        if (string.Equals(decision, "send-back", StringComparison.Ordinal)
            && result == WorkflowDispatchResult.Parked)
        {
            result = await _dispatcher.DispatchAsync(
                WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, InvoiceApprovalSteps.Decide, at),
                ct).ConfigureAwait(false);
        }

        return new InvoiceApprovalResumeResult(result, approvalDecision);
    }

    /// <summary>
    /// The approvers on record for this approve: the actor taking the action, plus the overriding party when
    /// one is claimed. The claim is RESOLVED through the same authorization gate that admitted this approve
    /// — the claimed actor must itself be allowed to post this very journal entry — and only the party the
    /// gate allows becomes <see cref="ApproverOnRecord.ResolvedApprover"/>. A claim the gate denies (or a
    /// blank one, which never reaches the gate) resolves to nobody, and the engine refuses the approve as
    /// <see cref="ApprovalRefusalReason.MissingOverrideApprover"/> — the refusal is the engine's, on the
    /// one decision, not a second verdict taken here. The engine still decides whether the override stands
    /// at all (a reason is mandatory, and the overrider may not be the requester).
    /// </summary>
    private async Task<IReadOnlyList<ApproverOnRecord>> ApproversOnRecordAsync(
        AuthorizationWriteContext writeAuthority,
        AuthorizationGateRequest postRequest,
        InvoiceApprovalOverride? conflictOverride,
        CancellationToken ct)
    {
        if (conflictOverride is not { } given)
            return [new ApproverOnRecord(writeAuthority.Principal, ResolvedApprover: writeAuthority.Principal)];

        var resolved = await ResolveOverridingPartyAsync(postRequest, given.Approver, ct).ConfigureAwait(false);
        return
        [
            new ApproverOnRecord(writeAuthority.Principal, ResolvedApprover: writeAuthority.Principal),
            new ApproverOnRecord(given.Approver, given.Reason, resolved),
        ];
    }

    /// <summary>
    /// Resolves a CLAIMED overriding party to a real one, or to nobody. The claim is put to the same
    /// <see cref="AuthorizationGate"/>, for the same act and the same journal entry, with the claimed actor
    /// as the principal: a party the gate does not allow to post this entry cannot stand as the second set
    /// of eyes on it. Returns null for a blank claim and for a denied one — the caller records that null
    /// and the engine names the refusal.
    /// </summary>
    private async Task<ActorId?> ResolveOverridingPartyAsync(
        AuthorizationGateRequest postRequest,
        ActorId claimed,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claimed.Value))
            return null;

        var decision = await _authorizationGate!
            .DecideAsync(postRequest with { Principal = claimed }, ct)
            .ConfigureAwait(false);
        return decision.Verdict is AuthorizationVerdict.Allowed ? claimed : (ActorId?)null;
    }

    /// <summary>
    /// The posting-period state for the journal entry this approve would post: the resolver is asked for the
    /// period covering the LIVE invoice's chart and issue date — the same chart and date
    /// <see cref="NodeLiveInvoiceApprovalContext.BuildPostEffect"/> stamps on the entry, and the same
    /// <see cref="IPeriodResolver"/> <c>JournalPostingService</c> gates the post on. The state is reported,
    /// never judged: only <see cref="SeparationOfDutyEngine"/> decides what a non-open period means.
    /// A period the resolver cannot find is <see cref="NoPeriodForDate"/>; an invoice that is gone is a
    /// period nobody resolved, recorded as the null it is.
    /// </summary>
    private async Task<string?> ResolvePostingPeriodAsync(
        TenantId tenant,
        string? stateJson,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var invoiceId = StateString(stateJson, "invoiceId");
        if (string.IsNullOrWhiteSpace(invoiceId))
            return null;

        var invoice = await _invoices!
            .GetAsync(tenant, new InvoiceId(invoiceId), at, ct)
            .ConfigureAwait(false);
        if (invoice is null)
            return null;

        var period = await _periods!
            .ResolveAsync(invoice.ChartId, invoice.IssueDate, ct)
            .ConfigureAwait(false);
        return period is { } covering ? covering.Status.ToString() : NoPeriodForDate;
    }

    /// <summary>
    /// The actor who issued the invoice, read back from the Process working state written at instantiation.
    /// An instance created before the field existed reads as the empty actor, which no approver equals — so
    /// a legacy instance records a pass rather than a fabricated conflict.
    /// </summary>
    private static ActorId RequestedBy(string? stateJson)
        => new(StateString(stateJson, "requestedBy") ?? string.Empty);

    /// <summary>One string field off the Process working state, or null when it is absent.</summary>
    private static string? StateString(string? stateJson, string name)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        using var document = JsonDocument.Parse(stateJson);
        return document.RootElement.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Records the approval through the one audit seam, carrying the separation-of-duty decision so the
    /// stored snapshot spells the outcome and, on a refusal, the reason it was refused for. The seam copies
    /// the five approval facts off the decision; nothing here derives one.
    /// </summary>
    private async Task RecordApprovalAsync(
        AuthorizationDecision admittedDecision,
        SeparationOfDutyDecision approval,
        string action,
        CancellationToken ct)
    {
        var signed = await _signer!.SignAsync(
            new AuditPayload(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = action,
                ["approved"] = approval.Approved,
            }),
            admittedDecision.DecidedAt,
            Guid.NewGuid(),
            ct).ConfigureAwait(false);

        await _audit!.AppendAuthorizedAsync(
            new AuditRecord(
                AuditId: Guid.NewGuid(),
                TenantId: admittedDecision.Request.Tenant,
                EventType: ApprovalDecidedEventType,
                OccurredAt: admittedDecision.DecidedAt,
                Payload: signed,
                AttestingSignatures: ImmutableArray<AttestingSignature>.Empty,
                Actor: admittedDecision.Request.Principal,
                Target: admittedDecision.Request.Target,
                Act: admittedDecision.Request.Act),
            admittedDecision,
            ct,
            approval).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the human-action trigger payload — <c>{ "decision": "...", "note": "..." }</c>. The handler
    /// reads <c>decision</c>; <c>note</c> is carried for the audit trail (the engine records the payload on
    /// the outcome event). Serialized minimally without a JSON dependency to keep the cutover allocation-light.
    /// </summary>
    private static string BuildHumanActionPayload(string decision, string? note)
    {
        var safeDecision = System.Text.Json.JsonEncodedText.Encode(decision).ToString();
        if (string.IsNullOrEmpty(note))
        {
            return $"{{\"decision\":\"{safeDecision}\"}}";
        }
        var safeNote = System.Text.Json.JsonEncodedText.Encode(note).ToString();
        return $"{{\"decision\":\"{safeDecision}\",\"note\":\"{safeNote}\"}}";
    }
}

/// <summary>
/// The result of routing an over-threshold invoice issue through the approval engine — the parked Process
/// instance id + how the <c>decide</c> dispatch resolved (expected <see cref="WorkflowDispatchResult.Parked"/>).
/// </summary>
/// <param name="InstanceId">The instantiated <c>invoice-approval</c> Process id (the resume / inbox key).</param>
/// <param name="DecideResult">The dispatch outcome of the <c>decide</c> trigger (Parked on success).</param>
/// <param name="Threshold">The approval threshold this routing decision was made under.</param>
/// <summary>A named override of a declared duty conflict — ADR 0067 clause 2 requires both halves.</summary>
/// <param name="Reason">Why the conflict was allowed to stand.</param>
/// <param name="Approver">The actor giving the override (never the requester).</param>
public readonly record struct InvoiceApprovalOverride(string? Reason, ActorId Approver);

/// <summary>
/// The outcome of a resume: how the dispatch resolved, and the separation-of-duty decision the approve was
/// made under (null for reject / send-back and for a cutover with no authorized-approve surface). A resume
/// whose <see cref="Approval"/> is present and not approved dispatched NOTHING — no journal entry posted.
/// </summary>
public readonly record struct InvoiceApprovalResumeResult(
    WorkflowDispatchResult Dispatch,
    SeparationOfDutyDecision? Approval)
{
    /// <summary>True when the approve was refused by the separation-of-duty engine.</summary>
    public bool RefusedBySeparationOfDuty => Approval is { Approved: false };
}

public readonly record struct InvoiceApprovalRouteResult(
    string InstanceId,
    WorkflowDispatchResult DecideResult,
    decimal Threshold);
