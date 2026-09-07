using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The node-side read model for the GENERIC pending-CP-effect-confirmation surface (ADR 0135 A1 / 0143
/// R1-F) — the Harborline App "Effect Confirmations" Inbox. Where <see cref="NodeParkedTaskQueryReadModel"/> lists
/// the TYPED verticals (invoice-approval / kg-action-approval), this lists every workflow instance the
/// <see cref="DeclarativeWorkflowInterpreter"/> parked on a CP human-task, keyed off the interpreter's FE-1
/// basis (<c>kind == "cp-approval-basis"</c> — <see cref="DeclarativeWorkflowInterpreter.CpApprovalBasisKind"/>).
/// The three-way-match seed (<see cref="NodeThreeWayMatchWorkflowSeed"/>) is the first driver (W-8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure read over the engine's own tables — no new schema.</b> A parked confirmation is a
/// <see cref="WorkflowInstanceRecord"/> row with <see cref="WorkflowStatus.Parked"/>; its FE-1 basis (the
/// pending CP capability + the typed-outcome verbs) is the payload of the most-recent <c>"Parked"</c>
/// <see cref="WorkflowEventRecord"/> the interpreter wrote (<see cref="DeclarativeWorkflowInterpreter.BuildBasis"/>
/// stamps the <c>cp-approval-basis</c> reason). The instance-specific effect preview (subject, amount, the JE
/// debit/credit lines the confirm will post) is derived from the instance's durable working state — the
/// authoritative source (ADR 0143 F-3: the effect is re-derived from the durable park, never a client payload).
/// </para>
/// <para>
/// <b>Definition-agnostic discriminator.</b> Selection keys on the interpreter's basis <c>kind</c>, NOT a
/// hard-coded definition key, so any authored definition the interpreter executes appears here automatically
/// (the invoice-approval / kg-action typed verticals carry a DIFFERENT basis shape and are excluded). v1 is a
/// bounded scan over the tenant's parked instances (single-operator node) — a completed/executed run has no
/// open confirmation and is not returned.
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092 defence-in-depth).</b> Every query is scoped by an explicit
/// <c>WHERE TenantId</c> — the node has no ambient query filter, so this predicate IS the per-org isolation
/// boundary (the same posture as <see cref="NodeParkedTaskQueryReadModel"/> and the financial read routes).
/// </para>
/// </remarks>
public sealed class NodeWorkflowConfirmationReadModel
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct over the recoverable <c>local-node.db</c> context factory.</summary>
    public NodeWorkflowConfirmationReadModel(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <summary>
    /// Lists the pending CP effect confirmations for <paramref name="tenantId"/>, newest park first — every
    /// interpreter-parked instance whose latest <c>"Parked"</c> event basis is a <c>cp-approval-basis</c>, each
    /// carrying its FE-1 basis + the instance-derived effect preview.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowConfirmationView>> ListPendingConfirmationsAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var parked = await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId.Value && i.Status == WorkflowStatus.Parked)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (parked.Count == 0)
        {
            return Array.Empty<WorkflowConfirmationView>();
        }

        var ids = parked.Select(p => p.Id).ToList();
        var parkEvents = await ctx.Set<WorkflowEventRecord>()
            .AsNoTracking()
            .Where(ev => ids.Contains(ev.InstanceId) && ev.EventType == "Parked")
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latestBasisByInstance = parkEvents
            .GroupBy(ev => ev.InstanceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(ev => ev.Seq).First().DataJson);

        var views = new List<WorkflowConfirmationView>(parked.Count);
        foreach (var instance in parked.OrderByDescending(p => p.UpdatedAt))
        {
            latestBasisByInstance.TryGetValue(instance.Id, out var basisJson);
            var view = WorkflowConfirmationView.TryFrom(instance, basisJson);
            // Only interpreter-parked CP confirmations (cp-approval-basis) — the typed verticals are excluded.
            if (view is not null)
            {
                views.Add(view);
            }
        }

        return views;
    }

    /// <summary>
    /// Loads a single pending CP confirmation by instance id (tenant-scoped), or <see langword="null"/> if it
    /// is not an interpreter-parked <c>cp-approval-basis</c> confirmation of this tenant (unknown /
    /// foreign-tenant / already-actioned / a typed vertical) — a uniform 404 with no cross-tenant existence leak.
    /// </summary>
    public async Task<WorkflowConfirmationView?> GetPendingConfirmationAsync(
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
                     && i.Status == WorkflowStatus.Parked,
                ct)
            .ConfigureAwait(false);

        if (instance is null)
        {
            return null;
        }

        var basisJson = await ctx.Set<WorkflowEventRecord>()
            .AsNoTracking()
            .Where(ev => ev.InstanceId == instanceId && ev.EventType == "Parked")
            .OrderByDescending(ev => ev.Seq)
            .Select(ev => ev.DataJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Null if the basis is not a cp-approval-basis (e.g. a parked invoice-approval / kg-action task) — the
        // generic confirm surface only owns interpreter-parked CP confirmations.
        return WorkflowConfirmationView.TryFrom(instance, basisJson);
    }
}

/// <summary>
/// A projected view of one pending CP effect confirmation — the instance + definition metadata, the pending
/// CP capability + typed-outcome verbs (from the interpreter's <c>cp-approval-basis</c>), and the
/// instance-derived effect preview (subject / amount / the JE debit-credit lines the confirm will post). This
/// is the engine-side contract the Harborline App "Effect Confirmations" surface renders (ADR 0135 §Prerequisites —
/// the basis BEFORE the confirm control).
/// </summary>
/// <param name="InstanceId">The Process instance id (the confirm/resume target).</param>
/// <param name="DefinitionKey">The authored workflow definition key (e.g. <c>three-way-match.v1</c>).</param>
/// <param name="DefinitionVersion">The pinned (D7) definition version the instance executes.</param>
/// <param name="Step">The human-task park state the instance is parked on (e.g. <c>review</c>).</param>
/// <param name="CapabilityRef">The pending CP capability the confirm builds through the broker-PEP.</param>
/// <param name="SubjectId">The domain subject the effect concerns (e.g. the vendor bill id), from instance state.</param>
/// <param name="Amount">The effect amount (the value the effect posts), from instance state.</param>
/// <param name="PostingPreview">A human-readable preview of the effect (the JE debit/credit lines), from instance state.</param>
/// <param name="Memo">The effect memo/description, from instance state.</param>
/// <param name="TypedOutcomes">The allowed decision verbs — approve / reject / send-back.</param>
/// <param name="Proposer">The proposer label — always the non-human workflow engine (SoD requires a distinct human).</param>
/// <param name="ActorRole">The role permitted to confirm (single-operator FinancialAdmin in v1).</param>
/// <param name="CreatedAt">When the Process (and so the confirmation) was created.</param>
public sealed record WorkflowConfirmationView(
    string InstanceId,
    string DefinitionKey,
    string DefinitionVersion,
    string Step,
    int Iteration,
    string? CapabilityRef,
    string? SubjectId,
    decimal Amount,
    string? PostingPreview,
    string? Memo,
    IReadOnlyList<string> TypedOutcomes,
    string Proposer,
    string ActorRole,
    DateTimeOffset CreatedAt)
{
    /// <summary>The single-operator role permitted to confirm a CP effect in v1 (the node FinancialAdmin).</summary>
    public const string V1ActorRole = "FinancialAdmin";

    /// <summary>The proposer of an autonomously-parked CP action — always the non-human workflow engine.</summary>
    public const string EngineProposer = "workflow-engine";

    /// <summary>
    /// Projects a parked instance + its FE-1 basis-event payload onto the view, IFF the basis is an
    /// interpreter <c>cp-approval-basis</c>. Returns <see langword="null"/> for any other basis (a typed
    /// vertical's parked task) so the generic surface owns only interpreter-parked CP confirmations. Tolerant of
    /// a missing/malformed effect state (defaults the preview to null) so a corrupt row never throws on the list
    /// path — the capability + verbs come from the basis, the preview values from the instance working state.
    /// </summary>
    public static WorkflowConfirmationView? TryFrom(WorkflowInstanceRecord instance, string? basisJson)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(basisJson))
        {
            return null;
        }

        string? capabilityRef = null;
        var verbs = new List<string>();
        try
        {
            using var basisDoc = JsonDocument.Parse(basisJson);
            var b = basisDoc.RootElement;
            // Discriminate: only the interpreter's cp-approval-basis belongs to this surface.
            if (!b.TryGetProperty("kind", out var kindEl)
                || kindEl.ValueKind != JsonValueKind.String
                || kindEl.GetString() != DeclarativeWorkflowInterpreter.CpApprovalBasisKind)
            {
                return null;
            }
            if (b.TryGetProperty("capabilityRef", out var cap) && cap.ValueKind == JsonValueKind.String)
            {
                capabilityRef = cap.GetString();
            }
            if (b.TryGetProperty("typedOutcomes", out var outs) && outs.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in outs.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        verbs.Add(el.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A malformed basis is not a valid cp-approval-basis — exclude it rather than throw on the list path.
            return null;
        }

        // The effect preview is derived from the instance's durable working state (the ledger-effect shape
        // { amount, debitAccount, creditAccount, memo } + an optional subject id). Best-effort — a capability
        // whose state does not carry these leaves the preview null (the capabilityRef + subject still render).
        string? subjectId = null;
        decimal amount = 0m;
        string? debit = null;
        string? credit = null;
        string? memo = null;
        if (!string.IsNullOrWhiteSpace(instance.StateJson) && instance.StateJson != "{}")
        {
            try
            {
                using var stateDoc = JsonDocument.Parse(instance.StateJson);
                var s = stateDoc.RootElement;
                subjectId = ReadString(s, "billId") ?? ReadString(s, "subjectId") ?? ReadString(s, "invoiceId");
                if (s.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number)
                {
                    amount = amt.GetDecimal();
                }
                debit = ReadString(s, "debitAccount");
                credit = ReadString(s, "creditAccount");
                memo = ReadString(s, "memo");
            }
            catch (JsonException)
            {
                // Tolerant — fall through with the defaults.
            }
        }

        var preview = BuildPostingPreview(debit, credit, amount);

        return new WorkflowConfirmationView(
            InstanceId:        instance.Id,
            DefinitionKey:     instance.DefinitionKey,
            DefinitionVersion: instance.DefinitionVersion,
            Step:              instance.CurrentStep,
            Iteration:         instance.Iteration,
            CapabilityRef:     capabilityRef,
            SubjectId:         subjectId,
            Amount:            amount,
            PostingPreview:    preview,
            Memo:              memo,
            TypedOutcomes:     verbs.Count > 0 ? verbs : new[] { "approve", "reject", "send-back" },
            Proposer:          EngineProposer,
            ActorRole:         V1ActorRole,
            CreatedAt:         instance.CreatedAt);
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>
    /// Builds the FE-1 posting preview — the balanced debit/credit lines the <c>ledger.post-journal-entry</c>
    /// effect will stage (mirrors <see cref="NodeLedgerPostingEffect"/>: Debit <paramref name="debit"/> /
    /// Credit <paramref name="credit"/>, both = <paramref name="amount"/>). Null when the state does not carry
    /// the ledger-effect accounts.
    /// </summary>
    private static string? BuildPostingPreview(string? debit, string? credit, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(debit) || string.IsNullOrWhiteSpace(credit))
        {
            return null;
        }
        var a = amount.ToString("N2", CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"DR {debit}  {a}\nCR {credit}  {a}");
    }
}
