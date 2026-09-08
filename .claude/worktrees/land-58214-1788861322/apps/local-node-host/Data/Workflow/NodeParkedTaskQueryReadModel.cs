using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The node-side read model for the ADR 0135 §2.8.2 Ask-bar <b>Inbox</b> — lists the durable
/// <see cref="WorkflowStatus.Parked"/> CP human-tasks (the over-threshold <c>invoice-approval</c> Processes
/// awaiting an approve / reject / send-back decision) together with their FE-1 <b>basis payload</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure read over the engine's own tables — no new schema.</b> A parked task is a row in
/// <c>workflow_instances</c> with <see cref="WorkflowStatus.Parked"/>; its FE-1 basis (the posting preview +
/// the decision-table row/version that fired + the typed-outcome set) is the payload of the most-recent
/// <c>"Parked"</c> event the handler wrote in <c>workflow_events</c> (<see cref="WorkflowStepOutcome.Park"/>
/// records the basis as the park reason). This read model joins the two so the Harborline App Ask-bar Inbox can
/// render the basis BEFORE the confirm control (FE-1, ADR 0135 §Prerequisites — the CP confirm surface).
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092 defence-in-depth).</b> Every query is scoped by an explicit
/// <c>WHERE TenantId</c> — the node has no ambient query filter, so this predicate IS the per-org isolation
/// boundary (the same posture as <see cref="NodeEfWorkflowStore"/> and the financial read routes).
/// </para>
/// <para>
/// <b>SC4-C2.</b> The only source is the recoverable <see cref="LocalNodeDbContext"/> (the workflow tables);
/// no kernel CRDT reader / per-team event log is touched.
/// </para>
/// </remarks>
public sealed class NodeParkedTaskQueryReadModel
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct over the recoverable <c>local-node.db</c> context factory.</summary>
    public NodeParkedTaskQueryReadModel(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <summary>
    /// Lists the parked <c>invoice-approval</c> human-tasks for <paramref name="tenantId"/>, newest park
    /// first, each carrying its FE-1 basis payload. Only instances currently
    /// <see cref="WorkflowStatus.Parked"/> on the <see cref="InvoiceApprovalSteps.Approve"/> step are
    /// returned — a Completed / Rejected / Running instance has no open human-task.
    /// </summary>
    public async Task<IReadOnlyList<ParkedHumanTaskView>> ListInvoiceApprovalTasksAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // The parked instances (the open human-tasks) for this definition + tenant. The approve step is the
        // only human-task step in the invoice-approval definition, so a Parked instance on it IS an open
        // CP task.
        var parked = await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId.Value
                        && i.DefinitionKey == InvoiceApprovalSteps.DefinitionKey
                        && i.Status == WorkflowStatus.Parked
                        && i.CurrentStep == InvoiceApprovalSteps.Approve)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (parked.Count == 0)
        {
            return Array.Empty<ParkedHumanTaskView>();
        }

        // The FE-1 basis is the payload of the LATEST "Parked" event on the approve step for each instance.
        // Pull every Parked-on-approve event for these instances in one query, then pick max-Seq per instance.
        var ids = parked.Select(p => p.Id).ToList();
        var parkEvents = await ctx.Set<WorkflowEventRecord>()
            .AsNoTracking()
            .Where(ev => ids.Contains(ev.InstanceId)
                         && ev.EventType == "Parked"
                         && ev.Step == InvoiceApprovalSteps.Approve)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latestBasisByInstance = parkEvents
            .GroupBy(ev => ev.InstanceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(ev => ev.Seq).First().DataJson);

        var views = new List<ParkedHumanTaskView>(parked.Count);
        foreach (var instance in parked.OrderByDescending(p => p.UpdatedAt))
        {
            latestBasisByInstance.TryGetValue(instance.Id, out var basisJson);
            views.Add(ParkedHumanTaskView.From(instance, basisJson));
        }

        return views;
    }

    /// <summary>
    /// Loads a single parked task by instance id (tenant-scoped), or <see langword="null"/> if it is not a
    /// parked <c>invoice-approval</c> task of this tenant (unknown / foreign-tenant / already actioned).
    /// </summary>
    public async Task<ParkedHumanTaskView?> GetInvoiceApprovalTaskAsync(
        TenantId tenantId,
        string instanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var instance = await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.Id == instanceId
                     && i.TenantId == tenantId.Value
                     && i.DefinitionKey == InvoiceApprovalSteps.DefinitionKey
                     && i.Status == WorkflowStatus.Parked
                     && i.CurrentStep == InvoiceApprovalSteps.Approve,
                ct)
            .ConfigureAwait(false);

        if (instance is null)
        {
            return null;
        }

        var basisJson = await ctx.Set<WorkflowEventRecord>()
            .AsNoTracking()
            .Where(ev => ev.InstanceId == instanceId
                         && ev.EventType == "Parked"
                         && ev.Step == InvoiceApprovalSteps.Approve)
            .OrderByDescending(ev => ev.Seq)
            .Select(ev => ev.DataJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return ParkedHumanTaskView.From(instance, basisJson);
    }

    // ── kg-action-approval — parked proposed CP actions (ADR 0135 KG-search Slice 2-actions) ─────────────

    /// <summary>
    /// Lists the parked <c>kg-action-approval</c> human-tasks for <paramref name="tenantId"/>, newest park
    /// first, each carrying its FE-1 basis (the proposal text + the proposed action + the grounding-path basis
    /// + the asserted/inferred provenance + the TAINT label). Only instances currently
    /// <see cref="WorkflowStatus.Parked"/> on the <see cref="GraphRagProposalSteps.Approve"/> step are returned.
    /// </summary>
    public async Task<IReadOnlyList<KgActionTaskView>> ListKgActionApprovalTasksAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var parked = await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId.Value
                        && i.DefinitionKey == GraphRagProposalSteps.DefinitionKey
                        && i.Status == WorkflowStatus.Parked
                        && i.CurrentStep == GraphRagProposalSteps.Approve)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (parked.Count == 0)
        {
            return Array.Empty<KgActionTaskView>();
        }

        var ids = parked.Select(p => p.Id).ToList();
        var parkEvents = await ctx.Set<WorkflowEventRecord>()
            .AsNoTracking()
            .Where(ev => ids.Contains(ev.InstanceId)
                         && ev.EventType == "Parked"
                         && ev.Step == GraphRagProposalSteps.Approve)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latestBasisByInstance = parkEvents
            .GroupBy(ev => ev.InstanceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(ev => ev.Seq).First().DataJson);

        var views = new List<KgActionTaskView>(parked.Count);
        foreach (var instance in parked.OrderByDescending(p => p.UpdatedAt))
        {
            latestBasisByInstance.TryGetValue(instance.Id, out var basisJson);
            views.Add(KgActionTaskView.From(instance, basisJson));
        }

        return views;
    }

    /// <summary>
    /// Loads a single parked <c>kg-action-approval</c> task by instance id (tenant-scoped), or
    /// <see langword="null"/> if it is not a parked task of this tenant (unknown / foreign-tenant /
    /// already-actioned) — a uniform 404 with no cross-tenant existence leak.
    /// </summary>
    public async Task<KgActionTaskView?> GetKgActionApprovalTaskAsync(
        TenantId tenantId,
        string instanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var instance = await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.Id == instanceId
                     && i.TenantId == tenantId.Value
                     && i.DefinitionKey == GraphRagProposalSteps.DefinitionKey
                     && i.Status == WorkflowStatus.Parked
                     && i.CurrentStep == GraphRagProposalSteps.Approve,
                ct)
            .ConfigureAwait(false);

        if (instance is null)
        {
            return null;
        }

        var basisJson = await ctx.Set<WorkflowEventRecord>()
            .AsNoTracking()
            .Where(ev => ev.InstanceId == instanceId
                         && ev.EventType == "Parked"
                         && ev.Step == GraphRagProposalSteps.Approve)
            .OrderByDescending(ev => ev.Seq)
            .Select(ev => ev.DataJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return KgActionTaskView.From(instance, basisJson);
    }
}

/// <summary>
/// A projected view of one parked CP human-task — the instance metadata + its FE-1 basis payload + the
/// allowed typed outcomes. This is the engine-side contract the Ask-bar Inbox renders (ADR 0135 §2.8.2).
/// </summary>
/// <param name="InstanceId">The Process instance id (the resume target).</param>
/// <param name="DefinitionKey">The Workflow definition key (always <c>invoice-approval</c> here).</param>
/// <param name="Step">The human-task step the instance is parked on (<c>approve</c>).</param>
/// <param name="InvoiceId">The invoice this approval gates (derived from the instance working state).</param>
/// <param name="Amount">The invoice amount the threshold rule evaluated.</param>
/// <param name="PostingPreview">The FE-1 posting preview (the debit/credit the post will produce).</param>
/// <param name="DecisionVersion">The pinned decision-table version that fired (D7).</param>
/// <param name="DecisionRow">The decision-table row that fired.</param>
/// <param name="DecisionOutcome">The decision outcome (<c>RequireApproval</c>).</param>
/// <param name="TypedOutcomes">The allowed actions — approve / reject / send-back.</param>
/// <param name="ActorRole">The role permitted to action the task (single-operator FinancialAdmin in v1).</param>
/// <param name="CreatedAt">When the Process (and so the task) was created.</param>
public sealed record ParkedHumanTaskView(
    string InstanceId,
    string DefinitionKey,
    string Step,
    string? InvoiceId,
    decimal Amount,
    string? PostingPreview,
    string? DecisionVersion,
    string? DecisionRow,
    string? DecisionOutcome,
    IReadOnlyList<string> TypedOutcomes,
    string ActorRole,
    DateTimeOffset CreatedAt)
{
    /// <summary>The single-operator role permitted to action a CP human-task in v1 (the node FinancialAdmin).</summary>
    public const string V1ActorRole = "FinancialAdmin";

    /// <summary>The fixed v1 typed-outcome set for an invoice-approval task.</summary>
    public static readonly IReadOnlyList<string> InvoiceApprovalOutcomes = new[] { "approve", "reject", "send-back" };

    /// <summary>
    /// Projects a parked instance + its FE-1 basis-event payload onto the view. Tolerant of a
    /// missing/malformed basis payload (defaults the preview/decision to null) so a corrupt event row never
    /// throws on the list path — the instance + invoice id + amount come from the instance working state,
    /// the authoritative source.
    /// </summary>
    public static ParkedHumanTaskView From(WorkflowInstanceRecord instance, string? basisJson)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // The invoice id + amount are read from the instance working state (the authoritative source the
        // route stamped at instantiation), NOT the basis event — the basis is a presentation projection.
        string? invoiceId = null;
        decimal amount = 0m;
        if (!string.IsNullOrWhiteSpace(instance.StateJson) && instance.StateJson != "{}")
        {
            try
            {
                using var stateDoc = JsonDocument.Parse(instance.StateJson);
                var root = stateDoc.RootElement;
                if (root.TryGetProperty("invoiceId", out var inv) && inv.ValueKind == JsonValueKind.String)
                {
                    invoiceId = inv.GetString();
                }
                if (root.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number)
                {
                    amount = amt.GetDecimal();
                }
            }
            catch (JsonException)
            {
                // Tolerant — fall through with the defaults.
            }
        }

        string? preview = null;
        string? version = null;
        string? row = null;
        string? outcome = null;
        if (!string.IsNullOrWhiteSpace(basisJson))
        {
            try
            {
                using var basisDoc = JsonDocument.Parse(basisJson);
                var b = basisDoc.RootElement;
                if (b.TryGetProperty("postingPreview", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    preview = p.GetString();
                }
                if (b.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    if (d.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        version = v.GetString();
                    }
                    if (d.TryGetProperty("row", out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        row = r.GetString();
                    }
                    if (d.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String)
                    {
                        outcome = o.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Tolerant — a malformed basis payload yields a null preview, never a list-path throw.
            }
        }

        return new ParkedHumanTaskView(
            InstanceId:      instance.Id,
            DefinitionKey:   instance.DefinitionKey,
            Step:            instance.CurrentStep,
            InvoiceId:       invoiceId,
            Amount:          amount,
            PostingPreview:  preview,
            DecisionVersion: version,
            DecisionRow:     row,
            DecisionOutcome: outcome,
            TypedOutcomes:   InvoiceApprovalOutcomes,
            ActorRole:       V1ActorRole,
            CreatedAt:       instance.CreatedAt);
    }
}

/// <summary>
/// A projected view of one parked <c>kg-action-approval</c> human-task — the proposed CP action + its FE-1
/// basis (proposal text + action summary + grounding-path basis + asserted/inferred provenance + the TAINT
/// label) + the allowed typed outcomes. The engine-side contract the Ask-bar Inbox renders for a proposed CP
/// action (ADR 0135 KG-search Slice 2-actions). The TAINT label is part of the rendered basis so the human
/// SEES that the proposal is untrusted-derived BEFORE the confirm control.
/// </summary>
/// <param name="InstanceId">The Process instance id (the resume target).</param>
/// <param name="DefinitionKey">The Workflow definition key (always <c>kg-action-approval</c> here).</param>
/// <param name="Step">The human-task step the instance is parked on (<c>approve</c>).</param>
/// <param name="ProposalText">The grounded proposal text (UNTRUSTED-derived; presentation only).</param>
/// <param name="ActionKind">The NAMED CP action kind being proposed (e.g. <c>draft-journal-entry</c>).</param>
/// <param name="ActionSummary">A short human-readable summary of the proposed action.</param>
/// <param name="GroundingRecordIds">The authorized record ids the proposal cited (the grounding-path basis).</param>
/// <param name="GroundedOnInferredEdge">True iff any cited edge was INFERRED (an AI hint, §2.9) — surfaced for the human.</param>
/// <param name="Taint">The taint label the proposal carries (always <c>untrusted-derived</c>).</param>
/// <param name="TypedOutcomes">The allowed actions — approve / reject / send-back.</param>
/// <param name="ActorRole">The role permitted to action the task (single-operator FinancialAdmin in v1).</param>
/// <param name="CreatedAt">When the Process (and so the task) was created.</param>
public sealed record KgActionTaskView(
    string InstanceId,
    string DefinitionKey,
    string Step,
    string? ProposalText,
    string? ActionKind,
    string? ActionSummary,
    IReadOnlyList<string> GroundingRecordIds,
    bool GroundedOnInferredEdge,
    string? Taint,
    IReadOnlyList<string> TypedOutcomes,
    string ActorRole,
    DateTimeOffset CreatedAt)
{
    /// <summary>The single-operator role permitted to action a CP human-task in v1 (the node FinancialAdmin).</summary>
    public const string V1ActorRole = "FinancialAdmin";

    /// <summary>The fixed v1 typed-outcome set for a kg-action-approval task.</summary>
    public static readonly IReadOnlyList<string> KgActionOutcomes = new[] { "approve", "reject", "send-back" };

    /// <summary>
    /// Projects a parked instance + its FE-1 basis-event payload onto the view. Tolerant of a missing/malformed
    /// basis payload (defaults the proposal/action fields) so a corrupt event row never throws on the list path.
    /// </summary>
    public static KgActionTaskView From(WorkflowInstanceRecord instance, string? basisJson)
    {
        ArgumentNullException.ThrowIfNull(instance);

        string? proposalText = null;
        string? actionKind = null;
        string? actionSummary = null;
        string? taint = null;
        var groundedOnInferred = false;
        var ids = new List<string>();

        if (!string.IsNullOrWhiteSpace(basisJson))
        {
            try
            {
                using var basisDoc = JsonDocument.Parse(basisJson);
                var b = basisDoc.RootElement;
                if (b.TryGetProperty("proposalText", out var pt) && pt.ValueKind == JsonValueKind.String)
                {
                    proposalText = pt.GetString();
                }
                if (b.TryGetProperty("action", out var act) && act.ValueKind == JsonValueKind.Object)
                {
                    if (act.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                    {
                        actionKind = k.GetString();
                    }
                    if (act.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                    {
                        actionSummary = s.GetString();
                    }
                }
                if (b.TryGetProperty("grounding", out var g) && g.ValueKind == JsonValueKind.Object)
                {
                    if (g.TryGetProperty("recordIds", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in idArr.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String)
                            {
                                ids.Add(el.GetString()!);
                            }
                        }
                    }
                    if (g.TryGetProperty("groundedOnInferredEdge", out var ie) && ie.ValueKind == JsonValueKind.True)
                    {
                        groundedOnInferred = true;
                    }
                }
                if (b.TryGetProperty("taint", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    taint = t.GetString();
                }
            }
            catch (JsonException)
            {
                // Tolerant — a malformed basis payload yields null fields, never a list-path throw.
            }
        }

        return new KgActionTaskView(
            InstanceId:             instance.Id,
            DefinitionKey:          instance.DefinitionKey,
            Step:                   instance.CurrentStep,
            ProposalText:           proposalText,
            ActionKind:             actionKind,
            ActionSummary:          actionSummary,
            GroundingRecordIds:     ids,
            GroundedOnInferredEdge: groundedOnInferred,
            Taint:                  taint,
            TypedOutcomes:          KgActionOutcomes,
            ActorRole:              V1ActorRole,
            CreatedAt:              instance.CreatedAt);
    }
}
