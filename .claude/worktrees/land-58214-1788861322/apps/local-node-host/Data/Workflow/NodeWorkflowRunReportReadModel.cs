using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// The node-side read model for the P6 <b>declarative reporting</b> surface — an executed-run / effect-ledger
/// report over the engine (goal §P6: "form-view-in-read-mode over the engine", declarative config over the
/// substrate, NOT a new package). Lists TERMINAL workflow runs (Completed / Failed) with their outcome + the
/// CP effect each executed run posted (derived from the interpreter's executed-effect event).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure read over the engine's own tables — no new schema.</b> A run is a
/// <see cref="WorkflowInstanceRecord"/> row (the run record); its effect ledger is the append-only
/// <c>workflow_events</c> log. An executed CP effect is the event whose payload the interpreter stamps
/// <c>{"executed":true, "capability":"…", "action":"…", "step":"…"}</c> (see
/// <c>DeclarativeWorkflowInterpreter.EffectResultJson</c>). This joins the two into a report row so a read-mode
/// Harborline App view can render "what ran, what it posted, when" — the run-list/effect-ledger the D-INV-7 health
/// picture and the W-17 observability model build on.
/// </para>
/// <para>
/// <b>Tenant boundary (ADR 0092 defence-in-depth).</b> Every query is scoped by an explicit
/// <c>WHERE TenantId</c> — the same posture as the confirmation + parked-task read models.
/// </para>
/// </remarks>
public sealed class NodeWorkflowRunReportReadModel
{
    private static readonly WorkflowStatus[] TerminalStatuses = { WorkflowStatus.Completed, WorkflowStatus.Failed };

    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct over the recoverable <c>local-node.db</c> context factory.</summary>
    public NodeWorkflowRunReportReadModel(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <summary>
    /// Lists the terminal (executed) runs for <paramref name="tenantId"/>, newest completion first, each with
    /// its outcome + the CP effect it posted (if any). A Running / Parked instance is an OPEN run and is not
    /// reported here (it belongs to the confirmations surface).
    /// </summary>
    public async Task<IReadOnlyList<WorkflowRunReportRow>> ListRunReportAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var runs = await ctx.Set<WorkflowInstanceRecord>()
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId.Value && TerminalStatuses.Contains(i.Status))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (runs.Count == 0)
        {
            return Array.Empty<WorkflowRunReportRow>();
        }

        var ids = runs.Select(r => r.Id).ToList();
        // Pull the events for the reported runs (one query) and find each run's executed-effect payload in
        // memory — the interpreter stamps {"executed":true, "capability":"…"} on the advance/complete outcome
        // that fired a CP effect. In-memory match (vs. a JSON LIKE) keeps the query provider-portable; the run
        // set is bounded on the single-operator node.
        var events = await ctx.Set<WorkflowEventRecord>()
            .AsNoTracking()
            .Where(ev => ids.Contains(ev.InstanceId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latestEffectByInstance = events
            .Where(ev => ev.DataJson is not null && ev.DataJson.Contains("\"executed\":true", StringComparison.Ordinal))
            .GroupBy(ev => ev.InstanceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(ev => ev.Seq).First().DataJson);

        var rows = new List<WorkflowRunReportRow>(runs.Count);
        foreach (var run in runs.OrderByDescending(r => r.UpdatedAt))
        {
            latestEffectByInstance.TryGetValue(run.Id, out var effectJson);
            rows.Add(WorkflowRunReportRow.From(run, effectJson));
        }

        return rows;
    }
}

/// <summary>
/// One executed-run report row — the run record + its outcome + the CP effect it posted. The engine-side
/// contract a read-mode Harborline App report renders (P6).
/// </summary>
/// <param name="InstanceId">The Process instance id.</param>
/// <param name="DefinitionKey">The authored workflow definition key.</param>
/// <param name="DefinitionVersion">The pinned (D7) definition version the run executed.</param>
/// <param name="Status">The terminal run status (<c>Completed</c> / <c>Failed</c>).</param>
/// <param name="FinalStep">The terminal state the run ended on (e.g. <c>posted</c> / <c>rejected</c>).</param>
/// <param name="SubjectId">The domain subject (e.g. the vendor bill id), from instance state.</param>
/// <param name="Amount">The effect amount, from instance state.</param>
/// <param name="Memo">The effect memo/description, from instance state.</param>
/// <param name="EffectPosted">True iff the run executed a CP effect (vs. a rejected/no-effect terminal).</param>
/// <param name="CapabilityRef">The CP capability the run's effect posted (null if none — e.g. a rejected run).</param>
/// <param name="CreatedAt">When the run was created.</param>
/// <param name="CompletedAt">When the run reached its terminal state.</param>
public sealed record WorkflowRunReportRow(
    string InstanceId,
    string DefinitionKey,
    string DefinitionVersion,
    string Status,
    string FinalStep,
    string? SubjectId,
    decimal Amount,
    string? Memo,
    bool EffectPosted,
    string? CapabilityRef,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt)
{
    /// <summary>Projects a terminal instance + its executed-effect event payload onto the report row.</summary>
    public static WorkflowRunReportRow From(WorkflowInstanceRecord instance, string? effectJson)
    {
        ArgumentNullException.ThrowIfNull(instance);

        string? subjectId = null;
        decimal amount = 0m;
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
                memo = ReadString(s, "memo");
            }
            catch (JsonException)
            {
                // Tolerant — a malformed state yields null preview fields, never a report-path throw.
            }
        }

        var effectPosted = false;
        string? capabilityRef = null;
        if (!string.IsNullOrWhiteSpace(effectJson))
        {
            try
            {
                using var effectDoc = JsonDocument.Parse(effectJson);
                var e = effectDoc.RootElement;
                if (e.TryGetProperty("executed", out var ex) && ex.ValueKind == JsonValueKind.True)
                {
                    effectPosted = true;
                }
                capabilityRef = ReadString(e, "capability");
            }
            catch (JsonException)
            {
                // Tolerant — fall through (a malformed effect event yields no capability).
            }
        }

        return new WorkflowRunReportRow(
            InstanceId:        instance.Id,
            DefinitionKey:     instance.DefinitionKey,
            DefinitionVersion: instance.DefinitionVersion,
            Status:            instance.Status.ToString(),
            FinalStep:         instance.CurrentStep,
            SubjectId:         subjectId,
            Amount:            amount,
            Memo:              memo,
            EffectPosted:      effectPosted,
            CapabilityRef:     capabilityRef,
            CreatedAt:         instance.CreatedAt,
            CompletedAt:       instance.UpdatedAt);
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
