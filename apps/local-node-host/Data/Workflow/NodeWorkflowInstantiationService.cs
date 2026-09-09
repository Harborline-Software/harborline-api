using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The instantiation surface (ADR 0135 v1) — the seam that CREATES durable <b>Process</b> instances from
/// real domain events / rows and wires them through the engine's <see cref="IWorkflowStore"/> so they are
/// durable + D7-pinned at instantiation. Completes the engine end-to-end: slice 2 built the two typed
/// handlers; this is what actually puts a Process into the store for the dispatcher + daemon to advance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a distinct opt-in path (NOT a hook on the existing direct-post routes).</b> The node already has
/// two direct-post financial paths: <c>InvoiceRoutes</c> issue (posts the AR JE inline) and
/// <c>NodeEfRecurringInvoiceService.GenerateDueInvoices</c> (posts the occurrence JE inline). The ADR-0135
/// handlers ALSO post a JE (<c>InvoiceApprovalHandler</c>'s post effect; <c>RecurringGenerationHandler</c>'s
/// generation effect). Running a Process <em>alongside</em> the existing inline post would DOUBLE-POST. So
/// this service is the engine-driven ALTERNATIVE post path a caller opts into — it is the engine's own
/// instantiation surface, not an auto-fire bolted onto the legacy routes. (The legacy direct-post routes
/// stay untouched; flipping a route to drive the engine instead is a separate, sequenced cutover — out of
/// scope here.)
/// </para>
/// <para>
/// <b>D7 pinning at instantiation.</b> An <c>invoice-approval</c> Process pins
/// <see cref="NodeWorkflowDefinitions.InvoiceApprovalV1Version"/> on
/// <see cref="WorkflowInstanceRecord.DefinitionVersion"/> at creation — replay always resolves the decision
/// table against THIS version (a replayed step must be deterministic). A future threshold change adds a new
/// effective-dated version without retroactively altering pinned instances.
/// </para>
/// <para>
/// <b>SC4-C2.</b> The only persistence sink is the recoverable <see cref="LocalNodeDbContext"/> (the
/// instance row via the <see cref="IWorkflowStore"/>; the schedule read via the same context). No kernel
/// CRDT writer / per-team event log is reachable.
/// </para>
/// </remarks>
public sealed class NodeWorkflowInstantiationService
{
    private readonly IWorkflowStore _store;
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct over the durable workflow store + the recoverable schedule-store context factory.</summary>
    public NodeWorkflowInstantiationService(
        IWorkflowStore store,
        IDbContextFactory<LocalNodeDbContext> contextFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  access-grant issuance — the admitted instance-creation path used after
    //  the Forms authorization gate has allowed the submission.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the Access pack's typed grant-issuance Process for a submitted form, pinning the pack's
    /// workflow revision. The submission instance id makes creation idempotent.
    /// </summary>
    internal async Task<string> StartAccessGrantIssuanceAsync(
        TenantId tenantId,
        string submissionInstanceId,
        GrantIssuanceRequest request,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(submissionInstanceId);
        ArgumentNullException.ThrowIfNull(request);

        var instanceId = "access-grant-form:" + submissionInstanceId;
        if (await _store.LoadAsync(instanceId, ct).ConfigureAwait(false) is not null)
        {
            return instanceId;
        }

        await _store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            TenantId = tenantId.Value,
            DefinitionKey = GrantIssuanceSteps.DefinitionKey,
            DefinitionVersion = "1.0.1",
            CurrentStep = GrantIssuanceSteps.Approve,
            Status = WorkflowStatus.Running,
            StateJson = GrantIssuanceHandler.SerializeRequest(request),
        }, at, ct).ConfigureAwait(false);

        return instanceId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  invoice-approval — on the invoice-issued path: create a Process for an invoice
    //  (the handler then runs under/over-threshold → park → post).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <c>invoice-approval</c> Process for an issued invoice, pinning the v1 decision-table version
    /// (D7) at instantiation. The instance starts on the <c>decide</c> step Running; dispatching its
    /// <c>decide</c> trigger runs the threshold rule (under → engine auto-posts; over → parks the
    /// human-task with the FE-1 basis). Returns the instance id (== <paramref name="invoiceId"/> namespaced),
    /// idempotent on that id (a re-instantiation for the same invoice is a no-op).
    /// </summary>
    /// <param name="tenantId">The tenant the invoice + Process are homed to (ADR 0135 D3 single-owner).</param>
    /// <param name="invoiceId">The invoice this approval Process gates the post of.</param>
    /// <param name="amount">The invoice total the threshold rule is evaluated against.</param>
    /// <param name="debitAccount">The GL account debited by the post effect (e.g. AR control).</param>
    /// <param name="creditAccount">The GL account credited by the post effect (e.g. income).</param>
    /// <param name="memo">A human-readable memo carried onto the posted JE.</param>
    /// <param name="requestedBy">
    /// The actor requesting the approval (the issuer). Ticket 272: a later approve is separated from THIS
    /// actor, so it is written onto the working state at instantiation rather than re-derived at approve
    /// time from whoever happens to be on the request.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> StartInvoiceApprovalAsync(
        TenantId tenantId,
        string invoiceId,
        decimal amount,
        string debitAccount,
        string creditAccount,
        string memo,
        DateTimeOffset at,
        CancellationToken ct = default,
        string requestedBy = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);

        var instanceId = "invoice-approval:" + invoiceId;

        // Idempotent on the instance id — re-instantiating the same invoice's approval is a no-op (the
        // single-owner home is the only creator, so a LoadAsync gate is race-free on a single device).
        if (await _store.LoadAsync(instanceId, ct).ConfigureAwait(false) is not null)
        {
            return instanceId;
        }

        var state = JsonSerializer.Serialize(new
        {
            invoiceId,
            amount,
            debitAccount,
            creditAccount,
            memo,
            requestedBy,
        });

        await _store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            TenantId = tenantId.Value,
            DefinitionKey = InvoiceApprovalSteps.DefinitionKey,
            // D7 — pin the decision-table version at instantiation.
            DefinitionVersion = NodeWorkflowDefinitions.InvoiceApprovalV1Version,
            CurrentStep = InvoiceApprovalSteps.Decide,
            Status = WorkflowStatus.Running,
            StateJson = state,
        }, at, ct).ConfigureAwait(false);

        return instanceId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  kg-action-approval — on a grounded GraphRAG proposed CP action: create a
    //  Process that PARKS the action to the human-CP-gate (ADR 0135 KG-search
    //  Slice 2-actions, G-G4). NOTHING executes without a human approve.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <c>kg-action-approval</c> Process for a grounded GraphRAG proposed CP action. The instance
    /// starts on the <c>decide</c> step Running; dispatching its <c>decide</c> trigger ALWAYS parks on the
    /// approve human-task (a proposed CP action is CP, full stop — there is NO auto-approve branch, unlike the
    /// invoice handler). The working state carries the FE-1 basis (the proposal text + the action summary + the
    /// grounding-path basis + the asserted/inferred provenance + the TAINT label) AND the structured action
    /// input the host CP executor consumes on approve. Returns the instance id, idempotent on it (a re-park of
    /// the same proposal is a no-op).
    /// </summary>
    /// <param name="tenantId">The tenant the proposal + Process are homed to (ADR 0135 D3 single-owner).</param>
    /// <param name="proposalId">A stable id for this proposed action (the instance-id namespacing key).</param>
    /// <param name="stateJson">
    /// The pre-serialized working state — <c>{ proposalText, actionKind, actionSummary, groundingRecordIds,
    /// groundedOnInferredEdge, taint, action: { debitAccount, creditAccount, amount, memo } }</c>. The cutover
    /// builds it from the parked <c>KgGenerationProposal</c> + its <c>KgProposedAction</c>; the host execute
    /// context re-reads the structured <c>action</c> from here (never an inbound argument — the proposal is
    /// untrusted).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> StartKgActionApprovalAsync(
        TenantId tenantId,
        string proposalId,
        string stateJson,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(proposalId);
        ArgumentException.ThrowIfNullOrEmpty(stateJson);

        var instanceId = "kg-action-approval:" + proposalId;

        // Idempotent on the instance id — re-parking the same proposal is a no-op (single-owner home).
        if (await _store.LoadAsync(instanceId, ct).ConfigureAwait(false) is not null)
        {
            return instanceId;
        }

        await _store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            TenantId = tenantId.Value,
            DefinitionKey = GraphRagProposalSteps.DefinitionKey,
            DefinitionVersion = "v1",
            CurrentStep = GraphRagProposalSteps.Decide,
            Status = WorkflowStatus.Running,
            StateJson = stateJson,
        }, at, ct).ConfigureAwait(false);

        return instanceId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  recurring-generation — create Process instances from existing
    //  RecurringInvoiceSchedule rows, so the schedule daemon drives them.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures a <c>recurring-generation</c> Process exists for every <see cref="RecurringScheduleStatus.Active"/>
    /// <see cref="RecurringInvoiceSchedule"/> of <paramref name="tenantId"/> — so the
    /// <c>WorkflowScheduleDaemon</c> (via <c>NodeRecurringScheduleSource</c>) actually drives generation off
    /// the engine. Each Process carries the schedule's RRULE + amount + GL accounts in its working state.
    /// Idempotent: a schedule that already has its Process is skipped (instance id is derived from the
    /// schedule id). Returns the number of Processes CREATED on this call.
    /// </summary>
    /// <remarks>
    /// The schedule's line templates can carry multiple lines; v1 generation posts a single balanced JE for
    /// the schedule's <b>total</b> recurring amount (the sum of the line-template amounts) against the
    /// schedule's AR account + a single income account — matching the engine handler's single-JE effect.
    /// (Per-line GL fan-out is the legacy <c>NodeEfRecurringInvoiceService</c> draft→issue path; the engine
    /// v1 handler posts the consolidated occurrence JE.)
    /// </remarks>
    public async Task<int> SyncRecurringGenerationProcessesAsync(
        TenantId tenantId,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var schedules = await ctx.Set<RecurringInvoiceSchedule>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Status == RecurringScheduleStatus.Active)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var created = 0;
        foreach (var schedule in schedules)
        {
            ct.ThrowIfCancellationRequested();
            if (await EnsureRecurringProcessAsync(tenantId, schedule, at, ct).ConfigureAwait(false))
            {
                created++;
            }
        }

        return created;
    }

    /// <summary>
    /// Creates the <c>recurring-generation</c> Process for one schedule if it does not already exist. Returns
    /// <see langword="true"/> when a new Process was created, <see langword="false"/> when it already existed
    /// (idempotent). Public so a create-schedule route can instantiate the Process for a single new schedule
    /// without re-scanning every schedule.
    /// </summary>
    public async Task<bool> EnsureRecurringProcessAsync(
        TenantId tenantId,
        RecurringInvoiceSchedule schedule,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var instanceId = "recurring-generation:" + schedule.Id.Value;
        if (await _store.LoadAsync(instanceId, ct).ConfigureAwait(false) is not null)
        {
            return false;
        }

        // The recurring amount = the sum of the schedule's line-template amounts (qty * unitPrice).
        var amount = schedule.LineTemplates.Sum(l => l.Quantity * l.UnitPrice);
        var incomeAccount = schedule.LineTemplates.Count > 0
            ? schedule.LineTemplates[0].IncomeAccountId.Value
            : "4000";

        var state = JsonSerializer.Serialize(new
        {
            scheduleId = schedule.Id.Value,
            rrule = schedule.RecurrenceRule,
            startsOn = schedule.StartsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            timezone = schedule.Timezone,
            amount,
            // The occurrence JE debits AR, credits income — mirroring the issue JE the legacy path posts.
            debitAccount = schedule.ArAccountId.Value,
            creditAccount = incomeAccount,
        });

        await _store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            TenantId = tenantId.Value,
            DefinitionKey = RecurringGenerationSteps.DefinitionKey,
            DefinitionVersion = "v1",
            // Start parked-ready on the schedule's first occurrence step; the daemon's source re-derives the
            // due generate@{date} steps from the RRULE, so the start step is informational.
            CurrentStep = RecurringGenerationSteps.GenerateStep(schedule.StartsOn),
            Status = WorkflowStatus.Running,
            StateJson = state,
        }, at, ct).ConfigureAwait(false);

        return true;
    }
}
