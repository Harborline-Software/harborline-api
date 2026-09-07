using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local GENERIC CP-effect-confirmation surface (ADR 0135 A1 / 0143 R1-F — the Harborline App "Effect
/// Confirmations" Inbox). The confirm surface for the DECLARATIVE interpreter, distinct from the typed
/// invoice-approval / kg-action verticals (<see cref="InvoiceApprovalTaskRoutes"/>). Two surfaces:
/// <list type="bullet">
///   <item><b>Query</b> — <c>GET /api/local-node/workflow-confirmations</c> lists every interpreter-parked CP
///     confirmation WITH its FE-1 basis (the pending CP capability + the definition/state it derives from + the
///     typed-outcome verbs); <c>GET /api/local-node/workflow-confirmations/{instanceId}</c> returns one.</item>
///   <item><b>Confirm</b> — <c>POST /api/local-node/workflow-confirmations/{instanceId}/action</c> confirms a
///     parked CP effect (<c>approve</c> → the SoD-gated broker builds the effect ONCE; <c>reject</c> → records
///     an override, no effect; <c>send-back</c> → the interpreter's bounded loop-back), resuming the engine via
///     the dispatcher. Idempotent on redelivery.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR 0143 F-3 — the route passes ONLY the decision verb.</b> The confirm body carries
/// <c>{ "decision": "approve" | "reject" | "send-back" }</c>; the interpreter re-derives the effect request
/// from the PINNED definition + the durable instance state (never a client-supplied effect payload), and the
/// confirmer identity is resolved SERVER-SIDE by <see cref="NodeWorkflowConfirmationContext"/> (party via
/// <c>IPartyContext</c>, human/agent via <c>IPrincipalKindResolver</c>). The engine proposer is a fixed
/// non-human party, so the broker's SoD rule ALWAYS requires a distinct human confirmer.
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + confirm resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c>. A foreign-tenant / unknown / already-actioned / non-interpreter
/// instance id resolves to a uniform 404 on both read + confirm (no cross-tenant existence leak) — and never
/// reaches the dispatcher.
/// </para>
/// <para>
/// <b>Wiring.</b> The read model + the trigger dispatcher are injected from the OUTER host container and passed
/// to <see cref="Map"/> as closed-over dependencies — NOT resolved via <c>[FromServices]</c>, which would fail
/// on the inner shared-app container (bug-2849, same as the invoice routes).
/// </para>
/// </remarks>
public static class DeclarativeConfirmRoutes
{
    /// <summary>Canonical route base for the generic CP-effect-confirmation surface.</summary>
    public const string RouteBase = "/api/local-node/workflow-confirmations";

    private static readonly string[] AllowedDecisions = { "approve", "reject", "send-back" };

    /// <summary>
    /// Maps the confirmation query + confirm routes onto <paramref name="app"/>, closing over the
    /// <paramref name="confirmations"/> read model + the workflow trigger <paramref name="dispatcher"/>.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        NodeWorkflowConfirmationReadModel confirmations,
        IWorkflowTriggerDispatcher dispatcher,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(confirmations);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(timeProvider);

        MapList(app, confirmations, activeTeam);
        MapDetail(app, confirmations, activeTeam);
        MapAction(app, confirmations, dispatcher, activeTeam, gate, timeProvider);
    }

    // ── GET /api/local-node/workflow-confirmations — list pending CP confirmations ──
    private static void MapList(IEndpointRouteBuilder app, NodeWorkflowConfirmationReadModel confirmations, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var tenantId = NodeTenant.Resolve(activeTeam);
            var views = await confirmations.ListPendingConfirmationsAsync(tenantId, ct).ConfigureAwait(false);
            return Results.Ok(new WorkflowConfirmationListResponse(
                Data: views.Select(WorkflowConfirmationWire.From).ToList()));
        });
    }

    // ── GET /api/local-node/workflow-confirmations/{instanceId} — one confirmation ──
    private static void MapDetail(IEndpointRouteBuilder app, NodeWorkflowConfirmationReadModel confirmations, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{instanceId}}", async (string instanceId, CancellationToken ct) =>
        {
            var tenantId = NodeTenant.Resolve(activeTeam);
            var view = await confirmations.GetPendingConfirmationAsync(tenantId, instanceId, ct).ConfigureAwait(false);
            return view is null
                ? Results.NotFound()
                : Results.Ok(new WorkflowConfirmationDetailResponse(WorkflowConfirmationWire.From(view)));
        });
    }

    // ── POST /api/local-node/workflow-confirmations/{instanceId}/action — confirm (resume the engine) ──
    private static void MapAction(
        IEndpointRouteBuilder app,
        NodeWorkflowConfirmationReadModel confirmations,
        IWorkflowTriggerDispatcher dispatcher,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{instanceId}}/action", async (
            string instanceId,
            ConfirmationActionRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Decision))
            {
                return Results.BadRequest(new { error = "decision_required", detail = "Body must carry a 'decision' of approve / reject / send-back." });
            }

            var decision = body.Decision!.Trim();
            if (Array.IndexOf(AllowedDecisions, decision) < 0)
            {
                return Results.BadRequest(new { error = "invalid_decision", detail = "decision must be one of: approve, reject, send-back." });
            }

            var tenantId = NodeTenant.Resolve(activeTeam);
            var authority = new AuthorizationWriteContext(
                new ActorId(NodeCallerParty.Resolve(http).Value),
                tenantId,
                timeProvider.GetUtcNow());
            var originatingDecision = await gate.DecideAsync(
                authority.Request(
                    AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
                    "record",
                    instanceId),
                ct).ConfigureAwait(false);
            originatingDecision.RequireAllowed();

            // Tenant + parked-state + cp-approval-basis gate: only an interpreter-parked CP confirmation of THIS
            // tenant is actionable. A foreign-tenant / unknown / already-actioned / typed-vertical instance
            // resolves to a uniform 404 (no existence leak) — and never reaches the dispatcher.
            var view = await confirmations.GetPendingConfirmationAsync(tenantId, instanceId, ct).ConfigureAwait(false);
            if (view is null)
            {
                return Results.NotFound();
            }

            // F-3: the payload carries ONLY the decision verb. The interpreter re-derives the effect from the
            // pinned definition + durable state; the confirmer is resolved server-side (never body-supplied).
            var payload = BuildDecisionPayload(decision, body.Note);

            AuthorizationDecision? ledgerDecision = null;
            if (string.Equals(view.CapabilityRef, NodeLedgerPostingEffect.CapabilityRef, StringComparison.Ordinal))
            {
                var journalId = NodeLedgerPostingEffect
                    .JournalEntryIdFor(view.InstanceId, view.Iteration, view.Step)
                    .Value;
                ledgerDecision = await gate.DecideAsync(
                    authority.Request(
                        AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost),
                        "journal-entry",
                        journalId),
                    ct).ConfigureAwait(false);
                ledgerDecision.RequireAllowed();
            }

            WorkflowDispatchResult result;
            try
            {
                result = await dispatcher.DispatchAsync(
                    WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, view.Step, authority.At, payload),
                    new WorkflowDispatchAuthority(originatingDecision, ledgerDecision),
                    ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A confirm-time failure (e.g. the SoD gate refused, or the effect build/post failed) aborts the
                // advance — the instance stays parked, NO effect committed. Surface as 422 so the operator can
                // retry after fixing the cause (no 500, no partial state).
                return Results.UnprocessableEntity(new { error = "confirm_failed", detail = "The confirmation could not be completed; the effect was not committed and the task remains parked." });
            }

            return result switch
            {
                // approve committed exactly one effect; reject recorded the override (both advance/complete).
                WorkflowDispatchResult.Advanced =>
                    Results.Ok(new ConfirmationActionResponse(instanceId, decision, "advanced")),
                // send-back looped the instance back to a prior state (the interpreter's bounded loop-back).
                WorkflowDispatchResult.Parked =>
                    Results.Ok(new ConfirmationActionResponse(instanceId, decision, "parked")),
                // A redelivered action on an already-completed instance — idempotent no-op (no double-effect).
                WorkflowDispatchResult.Terminal =>
                    Results.Ok(new ConfirmationActionResponse(instanceId, decision, "already_completed")),
                WorkflowDispatchResult.ReplayedNoOp =>
                    Results.Ok(new ConfirmationActionResponse(instanceId, decision, "replayed")),
                // The instance vanished between the gate read + the dispatch (race) — uniform 404.
                WorkflowDispatchResult.UnknownInstance => Results.NotFound(),
                _ => Results.UnprocessableEntity(new { error = "confirm_unexpected" }),
            };
        });
    }

    /// <summary>
    /// Builds the human-action trigger payload — <c>{ "decision": "..." }</c> (+ optional <c>note</c> for the
    /// audit trail). The interpreter reads ONLY <c>decision</c>; the note is carried onto the outcome event.
    /// </summary>
    private static string BuildDecisionPayload(string decision, string? note)
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

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>One pending CP effect confirmation in the Harborline App list (camelCase JSON; the FE-1 basis payload).</summary>
public sealed record WorkflowConfirmationWire(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("definitionKey")] string DefinitionKey,
    [property: JsonPropertyName("definitionVersion")] string DefinitionVersion,
    [property: JsonPropertyName("step")] string Step,
    [property: JsonPropertyName("capabilityRef")] string? CapabilityRef,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("postingPreview")] string? PostingPreview,
    [property: JsonPropertyName("memo")] string? Memo,
    [property: JsonPropertyName("proposer")] string Proposer,
    [property: JsonPropertyName("allowedOutcomes")] IReadOnlyList<string> AllowedOutcomes,
    [property: JsonPropertyName("actorRole")] string ActorRole,
    [property: JsonPropertyName("createdAt")] string CreatedAt)
{
    /// <summary>Projects the engine-side <see cref="WorkflowConfirmationView"/> onto the wire shape.</summary>
    public static WorkflowConfirmationWire From(WorkflowConfirmationView v) => new(
        InstanceId:        v.InstanceId,
        DefinitionKey:     v.DefinitionKey,
        DefinitionVersion: v.DefinitionVersion,
        Step:              v.Step,
        CapabilityRef:     v.CapabilityRef,
        SubjectId:         v.SubjectId,
        Amount:            (double)v.Amount,
        PostingPreview:    v.PostingPreview,
        Memo:              v.Memo,
        Proposer:          v.Proposer,
        AllowedOutcomes:   v.TypedOutcomes,
        ActorRole:         v.ActorRole,
        CreatedAt:         v.CreatedAt.ToString("O"));
}

/// <summary>List response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record WorkflowConfirmationListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<WorkflowConfirmationWire> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record WorkflowConfirmationDetailResponse(
    [property: JsonPropertyName("data")] WorkflowConfirmationWire Data);

/// <summary>POST body for <c>POST /api/local-node/workflow-confirmations/{instanceId}/action</c>.</summary>
public sealed record ConfirmationActionRequest(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("note")] string? Note);

/// <summary>Response to a confirm action — the instance id, the decision applied, and the dispatch outcome.</summary>
public sealed record ConfirmationActionResponse(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("outcome")] string Outcome);
