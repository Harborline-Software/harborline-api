using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local READ-ONLY declarative reporting surface (goal §P6 — an executed-run / effect-ledger report over
/// the engine). <c>GET /api/local-node/workflow-run-report</c> lists the terminal (executed) workflow runs +
/// the CP effect each posted. This is declarative reporting as config over the substrate (the run records +
/// the append-only effect log), NOT a new package — the run-list/observability primitive (W-17) the Harborline App
/// renders in read mode.
/// </summary>
/// <remarks>
/// Read-only (no confirm/resume — a terminal run has no open action). Tenant-scoped via
/// <c>NodeTenant.Resolve(activeTeam)</c>, injected + closed-over (bug-2849).
/// </remarks>
public static class WorkflowRunReportRoutes
{
    /// <summary>Canonical route base for the executed-run report surface.</summary>
    public const string RouteBase = "/api/local-node/workflow-run-report";

    /// <summary>Maps the report query route onto <paramref name="app"/>, closing over the <paramref name="report"/> read model.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        NodeWorkflowRunReportReadModel report,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(activeTeam);

        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var tenantId = NodeTenant.Resolve(activeTeam);
            var rows = await report.ListRunReportAsync(tenantId, ct).ConfigureAwait(false);
            return Results.Ok(new WorkflowRunReportResponse(
                Data: rows.Select(WorkflowRunReportWire.From).ToList()));
        });
    }
}

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>One executed-run report row (camelCase JSON).</summary>
public sealed record WorkflowRunReportWire(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("definitionKey")] string DefinitionKey,
    [property: JsonPropertyName("definitionVersion")] string DefinitionVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("finalStep")] string FinalStep,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("memo")] string? Memo,
    [property: JsonPropertyName("effectPosted")] bool EffectPosted,
    [property: JsonPropertyName("capabilityRef")] string? CapabilityRef,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("completedAt")] string CompletedAt)
{
    /// <summary>Projects the engine-side <see cref="WorkflowRunReportRow"/> onto the wire shape.</summary>
    public static WorkflowRunReportWire From(WorkflowRunReportRow r) => new(
        InstanceId:        r.InstanceId,
        DefinitionKey:     r.DefinitionKey,
        DefinitionVersion: r.DefinitionVersion,
        Status:            r.Status,
        FinalStep:         r.FinalStep,
        SubjectId:         r.SubjectId,
        Amount:            (double)r.Amount,
        Memo:              r.Memo,
        EffectPosted:      r.EffectPosted,
        CapabilityRef:     r.CapabilityRef,
        CreatedAt:         r.CreatedAt.ToString("O"),
        CompletedAt:       r.CompletedAt.ToString("O"));
}

/// <summary>Report response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record WorkflowRunReportResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<WorkflowRunReportWire> Data);
