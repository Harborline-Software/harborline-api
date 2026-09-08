using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local CP human-task surface (ADR 0135 §2.8.2 — the Ask-bar <b>Inbox</b>) for the KG-search
/// Slice 2-actions vertical flow: a grounded GraphRAG <b>proposed CP action</b> parked to the human-CP-gate.
/// Two surfaces:
/// <list type="bullet">
///   <item><b>Query</b> — <c>GET /api/local-node/kg-action-tasks</c> lists the parked
///     <c>kg-action-approval</c> human-tasks WITH their FE-1 basis (the proposal text + the proposed action +
///     the grounding-path basis + the asserted/inferred provenance + the TAINT label + the allowed outcomes);
///     <c>GET /api/local-node/kg-action-tasks/{instanceId}</c> returns one.</item>
///   <item><b>Resume</b> — <c>POST /api/local-node/kg-action-tasks/{instanceId}/action</c> actions a parked
///     task (<c>approve</c> → executes via the existing CP path EXACTLY ONCE; <c>reject</c> → nothing runs;
///     <c>send-back</c> → round-trip), resuming the engine via the cutover. Idempotent on redelivery.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>G-G4 — no one-click CP; the basis (incl. the taint) renders BEFORE the confirm.</b> The task view carries
/// the proposal's basis — the proposal text, the proposed action, the grounding it cited, the inferred-edge
/// provenance, and the TAINT label — so the Harborline App Ask-bar Inbox shows the basis BEFORE the confirm control
/// (FE-1, ADR 0135 §Prerequisites — the same basis-before-confirm posture as
/// <see cref="InvoiceApprovalTaskRoutes"/>). There is NO endpoint that executes a proposed action without a
/// human decision: the ONLY way to reach the engine's execute step is this resume route's <c>approve</c>.
/// </para>
/// <para>
/// <b>Injection defense.</b> An injected grounding that produced a malicious proposed action lands in this
/// Inbox like any other — its basis (incl. the injected proposal text + the untrusted-derived taint label) is
/// rendered for the human, who rejects it. There is no autonomous action even when the grounding is adversarial.
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + resume resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c>; a foreign-tenant / unknown / already-actioned instance resolves to a
/// uniform 404 on both read + resume (no cross-tenant existence leak). Wiring mirrors
/// <see cref="InvoiceApprovalTaskRoutes"/>: dependencies are closed-over (NOT <c>[FromServices]</c>; bug-2849).
/// </para>
/// </remarks>
public static class KgActionApprovalTaskRoutes
{
    /// <summary>Canonical route base for the parked kg-action-task surface.</summary>
    public const string RouteBase = "/api/local-node/kg-action-tasks";

    /// <summary>
    /// Maps the kg-action-task query + resume routes onto <paramref name="app"/>, closing over the parked-task
    /// <paramref name="tasks"/> read model + the <paramref name="cutover"/> resume coordinator.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        NodeParkedTaskQueryReadModel tasks,
        NodeKgActionApprovalCutover cutover,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(cutover);
        ArgumentNullException.ThrowIfNull(activeTeam);

        MapList(app, tasks, activeTeam);
        MapDetail(app, tasks, activeTeam);
        MapAction(app, tasks, cutover, activeTeam);
    }

    // ── GET /api/local-node/kg-action-tasks — list parked kg-action-approval tasks ────
    private static void MapList(IEndpointRouteBuilder app, NodeParkedTaskQueryReadModel tasks, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var tenantId = NodeTenant.Resolve(activeTeam);
            var views = await tasks.ListKgActionApprovalTasksAsync(tenantId, ct).ConfigureAwait(false);
            return Results.Ok(new KgActionTaskListResponse(
                Data: views.Select(KgActionTaskWire.From).ToList()));
        });
    }

    // ── GET /api/local-node/kg-action-tasks/{instanceId} — one task ─────────────────
    private static void MapDetail(IEndpointRouteBuilder app, NodeParkedTaskQueryReadModel tasks, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{instanceId}}", async (string instanceId, CancellationToken ct) =>
        {
            var tenantId = NodeTenant.Resolve(activeTeam);
            var view = await tasks.GetKgActionApprovalTaskAsync(tenantId, instanceId, ct).ConfigureAwait(false);
            return view is null
                ? Results.NotFound()
                : Results.Ok(new KgActionTaskDetailResponse(KgActionTaskWire.From(view)));
        });
    }

    // ── POST /api/local-node/kg-action-tasks/{instanceId}/action — resume the engine ─
    private static void MapAction(
        IEndpointRouteBuilder app,
        NodeParkedTaskQueryReadModel tasks,
        NodeKgActionApprovalCutover cutover,
        IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{instanceId}}/action", async (
            string instanceId,
            KgActionTaskActionRequest? body,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Decision))
            {
                return Results.BadRequest(new { error = "decision_required", detail = "Body must carry a 'decision' of approve / reject / send-back." });
            }

            var decision = body.Decision!.Trim();
            if (decision is not ("approve" or "reject" or "send-back"))
            {
                return Results.BadRequest(new { error = "invalid_decision", detail = "decision must be one of: approve, reject, send-back." });
            }

            var tenantId = NodeTenant.Resolve(activeTeam);

            // Tenant + parked-state gate: only a parked kg-action-approval task of THIS tenant is actionable.
            // A foreign-tenant / unknown / already-actioned instance resolves to a uniform 404 — and never
            // reaches the dispatcher (the ONLY path to execute is an approve on a real parked task of yours).
            var view = await tasks.GetKgActionApprovalTaskAsync(tenantId, instanceId, ct).ConfigureAwait(false);
            if (view is null)
            {
                return Results.NotFound();
            }

            WorkflowDispatchResult result;
            try
            {
                result = await cutover.ResumeAsync(instanceId, decision, body.Note, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A execute-time failure (e.g. an unknown action kind / malformed payload caught at the execute
                // effect) aborts the advance — the instance stays parked, NOTHING executed. Surface as 422.
                return Results.UnprocessableEntity(new { error = "resume_failed", detail = "The action could not be completed; the task remains parked and nothing was executed." });
            }

            return result switch
            {
                WorkflowDispatchResult.Advanced =>
                    Results.Ok(new KgActionTaskActionResponse(instanceId, decision, "advanced")),
                WorkflowDispatchResult.Parked =>
                    Results.Ok(new KgActionTaskActionResponse(instanceId, decision, "parked")),
                WorkflowDispatchResult.Terminal =>
                    Results.Ok(new KgActionTaskActionResponse(instanceId, decision, "already_completed")),
                WorkflowDispatchResult.ReplayedNoOp =>
                    Results.Ok(new KgActionTaskActionResponse(instanceId, decision, "replayed")),
                WorkflowDispatchResult.UnknownInstance => Results.NotFound(),
                _ => Results.UnprocessableEntity(new { error = "resume_unexpected" }),
            };
        });
    }
}

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>One parked proposed-CP-action task in the Inbox list (camelCase JSON; the FE-1 basis incl. taint).</summary>
public sealed record KgActionTaskWire(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("definitionKey")] string DefinitionKey,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("proposalText")] string? ProposalText,
    [property: JsonPropertyName("actionKind")] string? ActionKind,
    [property: JsonPropertyName("actionSummary")] string? ActionSummary,
    [property: JsonPropertyName("groundingRecordIds")] IReadOnlyList<string> GroundingRecordIds,
    [property: JsonPropertyName("groundedOnInferredEdge")] bool GroundedOnInferredEdge,
    [property: JsonPropertyName("taint")] string? Taint,
    [property: JsonPropertyName("allowedOutcomes")] IReadOnlyList<string> AllowedOutcomes,
    [property: JsonPropertyName("actorRole")] string ActorRole,
    [property: JsonPropertyName("createdAt")] string CreatedAt)
{
    /// <summary>Projects the engine-side <see cref="KgActionTaskView"/> onto the wire shape.</summary>
    public static KgActionTaskWire From(KgActionTaskView v) => new(
        InstanceId:             v.InstanceId,
        DefinitionKey:          v.DefinitionKey,
        Step:                   v.Step,
        ProposalText:           v.ProposalText,
        ActionKind:             v.ActionKind,
        ActionSummary:          v.ActionSummary,
        GroundingRecordIds:     v.GroundingRecordIds,
        GroundedOnInferredEdge: v.GroundedOnInferredEdge,
        Taint:                  v.Taint,
        AllowedOutcomes:        v.TypedOutcomes,
        ActorRole:              v.ActorRole,
        CreatedAt:              v.CreatedAt.ToString("O"));
}

/// <summary>List response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record KgActionTaskListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<KgActionTaskWire> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record KgActionTaskDetailResponse(
    [property: JsonPropertyName("data")] KgActionTaskWire Data);

/// <summary>POST body for <c>POST /api/local-node/kg-action-tasks/{instanceId}/action</c>.</summary>
public sealed record KgActionTaskActionRequest(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("note")] string? Note);

/// <summary>Response to a resume action — the instance id, the decision applied, and the dispatch outcome.</summary>
public sealed record KgActionTaskActionResponse(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("outcome")] string Outcome);
