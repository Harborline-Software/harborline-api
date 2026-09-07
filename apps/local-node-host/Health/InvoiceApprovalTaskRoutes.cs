using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local CP human-task surface (ADR 0135 §2.8.2 — the Ask-bar <b>Inbox</b>) for the invoice-approval
/// vertical flow. Two surfaces:
/// <list type="bullet">
///   <item><b>Query</b> — <c>GET /api/local-node/approval-tasks</c> lists the parked over-threshold
///     <c>invoice-approval</c> human-tasks WITH their FE-1 basis payload (posting preview + the
///     decision-table row/version that fired + the allowed outcomes + actor/role + createdAt);
///     <c>GET /api/local-node/approval-tasks/{instanceId}</c> returns one.</item>
///   <item><b>Resume</b> — <c>POST /api/local-node/approval-tasks/{instanceId}/action</c> actions a parked
///     task (<c>approve</c> → posts EXACTLY ONCE; <c>reject</c> → no post; <c>send-back</c> → round-trip),
///     resuming the engine via the dispatcher. Idempotent on redelivery.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + resume resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> — the same posture as <see cref="InvoiceRoutes"/> /
/// <see cref="JournalEntryRoutes"/>. The query read model applies an explicit <c>WHERE TenantId</c> (the
/// node's defence-in-depth boundary); a foreign-tenant / unknown instance id resolves to a uniform 404 on
/// both read + resume (no cross-tenant existence leak).
/// </para>
/// <para>
/// <b>FE-1 (binding, ADR 0135 §Prerequisites).</b> The task view renders the proposal's BASIS — the posting
/// preview + the decision-table row/version that fired (D7) — so the Harborline App Ask-bar Inbox can show the
/// basis BEFORE the confirm control. The resume surface is the confirm; one-click confirm is NOT offered for
/// this CP class (the basis-presenting task IS the gate).
/// </para>
/// <para>
/// <b>Wiring.</b> The query read model + the cutover coordinator are injected from the OUTER host container
/// and passed to <see cref="Map"/> as closed-over dependencies — NOT resolved via <c>[FromServices]</c>,
/// which would fail on the inner shared-app container (bug-2849, same as the invoice routes).
/// </para>
/// </remarks>
public static class InvoiceApprovalTaskRoutes
{
    /// <summary>Canonical route base for the parked-approval-task surface (kept in sync with <see cref="InvoiceRoutes.ApprovalTasksRouteBase"/>).</summary>
    public const string RouteBase = "/api/local-node/approval-tasks";

    /// <summary>
    /// Maps the approval-task query + resume routes onto <paramref name="app"/>, closing over the parked-task
    /// <paramref name="tasks"/> read model + the <paramref name="cutover"/> resume coordinator.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        NodeParkedTaskQueryReadModel tasks,
        NodeInvoiceApprovalCutover cutover,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(cutover);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(timeProvider);

        MapList(app, tasks, activeTeam);
        MapDetail(app, tasks, activeTeam);
        MapAction(app, tasks, cutover, activeTeam, timeProvider);
    }

    // ── GET /api/local-node/approval-tasks — list parked invoice-approval tasks ────
    private static void MapList(IEndpointRouteBuilder app, NodeParkedTaskQueryReadModel tasks, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var tenantId = NodeTenant.Resolve(activeTeam);
            var views = await tasks.ListInvoiceApprovalTasksAsync(tenantId, ct).ConfigureAwait(false);
            return Results.Ok(new ApprovalTaskListResponse(
                Data: views.Select(ApprovalTaskWire.From).ToList()));
        });
    }

    // ── GET /api/local-node/approval-tasks/{instanceId} — one task ─────────────────
    private static void MapDetail(IEndpointRouteBuilder app, NodeParkedTaskQueryReadModel tasks, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{instanceId}}", async (string instanceId, CancellationToken ct) =>
        {
            var tenantId = NodeTenant.Resolve(activeTeam);
            var view = await tasks.GetInvoiceApprovalTaskAsync(tenantId, instanceId, ct).ConfigureAwait(false);
            return view is null
                ? Results.NotFound()
                : Results.Ok(new ApprovalTaskDetailResponse(ApprovalTaskWire.From(view)));
        });
    }

    // ── POST /api/local-node/approval-tasks/{instanceId}/action — resume the engine ─
    /// <summary>
    /// The override offered on the body, if any. A body carrying either half offers an override — the
    /// engine, not this route, refuses the half-filled one (a blank reason or an unnamed overrider is a
    /// recorded refusal reason, not a silently dropped field).
    /// </summary>
    private static InvoiceApprovalOverride? OverrideFrom(ApprovalTaskActionRequest body) =>
        body.OverrideReason is null && body.OverrideApprover is null
            ? null
            : new InvoiceApprovalOverride(body.OverrideReason, new ActorId(body.OverrideApprover ?? string.Empty));

    private static void MapAction(
        IEndpointRouteBuilder app,
        NodeParkedTaskQueryReadModel tasks,
        NodeInvoiceApprovalCutover cutover,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{instanceId}}/action", async (
            string instanceId,
            ApprovalTaskActionRequest? body,
            HttpContext http,
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
            var at = timeProvider.GetUtcNow();

            // Tenant + parked-state gate: only a parked invoice-approval task of THIS tenant is actionable.
            // A foreign-tenant / unknown / already-actioned instance resolves to a uniform 404 (no existence
            // leak), exactly mirroring the read surface — and never reaches the dispatcher.
            var view = await tasks.GetInvoiceApprovalTaskAsync(tenantId, instanceId, ct).ConfigureAwait(false);
            if (view is null)
            {
                return Results.NotFound();
            }

            InvoiceApprovalResumeResult resumed;
            try
            {
                resumed = await cutover.ResumeAsync(
                    instanceId,
                    decision,
                    body.Note,
                    at,
                    ct,
                    FinancialRouteWriteAuthority.Create(http, tenantId, at),
                    OverrideFrom(body)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A post-time failure (e.g. the approve-effect issue rejected by the posting service) aborts
                // the advance — the instance stays parked, NO JE posted. Surface as 422 so the operator can
                // retry after fixing the cause (no 500 / no partial state).
                return Results.UnprocessableEntity(new { error = "resume_failed", detail = "The approval action could not be completed; the task remains parked and no journal entry was posted." });
            }

            // ADR 0067 clause 2 — the separation-of-duty engine refused this approve. Nothing dispatched and
            // no journal entry posted; the task stays parked. 409, with the reason the engine recorded.
            if (resumed.RefusedBySeparationOfDuty)
            {
                return Results.Conflict(new
                {
                    error = "separation_of_duty_refused",
                    reason = resumed.Approval!.RefusalReason.ToString(),
                });
            }

            var result = resumed.Dispatch;
            return result switch
            {
                // approve / reject committed (approve posted exactly one JE; reject posted none); send-back
                // round-tripped back to decide and re-parked (also an Advanced→Park internal step, surfaced
                // here as the dispatch's terminal/parked outcome).
                WorkflowDispatchResult.Advanced =>
                    Results.Ok(new ApprovalTaskActionResponse(instanceId, decision, "advanced")),
                WorkflowDispatchResult.Parked =>
                    Results.Ok(new ApprovalTaskActionResponse(instanceId, decision, "parked")),
                // A redelivered action on an already-completed instance — idempotent no-op (no double-post).
                WorkflowDispatchResult.Terminal =>
                    Results.Ok(new ApprovalTaskActionResponse(instanceId, decision, "already_completed")),
                WorkflowDispatchResult.ReplayedNoOp =>
                    Results.Ok(new ApprovalTaskActionResponse(instanceId, decision, "replayed")),
                // The task vanished between the gate read + the dispatch (race) — uniform 404.
                WorkflowDispatchResult.UnknownInstance => Results.NotFound(),
                _ => Results.UnprocessableEntity(new { error = "resume_unexpected" }),
            };
        });
    }
}

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>One parked CP human-task in the Inbox list (camelCase JSON; the FE-1 basis payload).</summary>
public sealed record ApprovalTaskWire(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("definitionKey")] string DefinitionKey,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("invoiceId")] string? InvoiceId,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("postingPreview")] string? PostingPreview,
    [property: JsonPropertyName("decisionVersion")] string? DecisionVersion,
    [property: JsonPropertyName("decisionRow")] string? DecisionRow,
    [property: JsonPropertyName("decisionOutcome")] string? DecisionOutcome,
    [property: JsonPropertyName("allowedOutcomes")] IReadOnlyList<string> AllowedOutcomes,
    [property: JsonPropertyName("actorRole")] string ActorRole,
    [property: JsonPropertyName("createdAt")] string CreatedAt)
{
    /// <summary>Projects the engine-side <see cref="ParkedHumanTaskView"/> onto the wire shape.</summary>
    public static ApprovalTaskWire From(ParkedHumanTaskView v) => new(
        InstanceId:      v.InstanceId,
        DefinitionKey:   v.DefinitionKey,
        Step:            v.Step,
        InvoiceId:       v.InvoiceId,
        Amount:          (double)v.Amount,
        PostingPreview:  v.PostingPreview,
        DecisionVersion: v.DecisionVersion,
        DecisionRow:     v.DecisionRow,
        DecisionOutcome: v.DecisionOutcome,
        AllowedOutcomes: v.TypedOutcomes,
        ActorRole:       v.ActorRole,
        CreatedAt:       v.CreatedAt.ToString("O"));
}

/// <summary>List response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record ApprovalTaskListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<ApprovalTaskWire> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record ApprovalTaskDetailResponse(
    [property: JsonPropertyName("data")] ApprovalTaskWire Data);

/// <summary>POST body for <c>POST /api/local-node/approval-tasks/{instanceId}/action</c>.</summary>
public sealed record ApprovalTaskActionRequest(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("overrideReason")] string? OverrideReason = null,
    [property: JsonPropertyName("overrideApprover")] string? OverrideApprover = null);

/// <summary>Response to a resume action — the instance id, the decision applied, and the dispatch outcome.</summary>
public sealed record ApprovalTaskActionResponse(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("outcome")] string Outcome);
