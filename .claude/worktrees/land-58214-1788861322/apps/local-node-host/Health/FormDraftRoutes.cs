using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Drafts;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// D2 node-local save-and-resume submission-draft routes (ADR 0135 amendment 2026-07-01).
/// The Harborline App form renderer PUTs partial values to a client-minted case id, resumes them
/// with GET, and abandons with DELETE. Keying + fail-closed party resolution happen inside
/// <see cref="ISubmissionDraftService"/>.
/// </summary>
/// <remarks>
/// <para><b>Routes:</b>
/// <list type="bullet">
///   <item><c>PUT    /api/local-node/forms/{formId}/drafts/{caseId}</c> — save/replace the draft.</item>
///   <item><c>GET    /api/local-node/forms/{formId}/drafts/{caseId}</c> — resume the draft (or 204 when none).</item>
///   <item><c>DELETE /api/local-node/forms/{formId}/drafts/{caseId}</c> — abandon the draft.</item>
///   <item><c>GET    /api/local-node/forms/drafts</c> — list the operator's resumable drafts.</item>
/// </list>
/// </para>
/// <para>
/// <b>Scoped service, per-request scope (bug-2849).</b> The draft service is SCOPED (it reads the
/// scoped ambient principal for fail-closed party resolution), and these routes are mapped on the
/// SharedHostedWebApp's INNER container. So the mapper closes over the OUTER root provider and
/// creates a scope per request to resolve <see cref="ISubmissionDraftService"/> + the form-definition
/// store, exactly the container the outer registrations live in.
/// </para>
/// <para>
/// <b>Fail-closed.</b> An unresolved principal throws <see cref="PrincipalPartyResolutionException"/>
/// inside the service → the route returns 403 (a mis-provisioning condition, not normal flow).
/// </para>
/// </remarks>
public static class FormDraftRoutes
{
    /// <summary>Canonical route base for the node-local forms surface.</summary>
    public const string RouteBase = "/api/local-node/forms";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] DefaultLocaleChain = { "en-US" };

    /// <summary>Maps the draft routes, closing over the outer root provider for per-request scopes.</summary>
    public static void Map(IEndpointRouteBuilder app, IServiceProvider rootServices)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(rootServices);

        // PUT — save/replace a draft under a client-minted case id.
        app.MapPut($"{RouteBase}/{{formId}}/drafts/{{caseId}}", async (
            string formId, string caseId, DraftSaveRequest request, CancellationToken ct) =>
        {
            if (!DraftCaseId.TryCreate(caseId, out var cid))
            {
                return Results.BadRequest(new { code = "form_draft.case_id_malformed" });
            }
            if (request?.Values.ValueKind is not JsonValueKind.Object)
            {
                return Results.BadRequest(new { code = "form_draft.values_required" });
            }

            using var scope = rootServices.CreateScope();
            var sp = scope.ServiceProvider;
            var drafts = sp.GetRequiredService<ISubmissionDraftService>();
            var defStore = sp.GetRequiredService<IFormDefinitionStore>();
            var activeTeam = sp.GetRequiredService<IActiveTeamAccessor>();

            var tenant = NodeTenant.Resolve(activeTeam);
            var formDef = await defStore.GetCurrentPublishedAsync(
                new DefinitionAddress(tenant, formId), ct).ConfigureAwait(false);
            if (formDef is null)
            {
                return Results.NotFound(new { code = "form_definition.not_published", detail = new { formId } });
            }

            var provenance = SubmissionDraftProvenance.Create(formDef, DefaultLocaleChain);
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(request.Values, JsonOptions);

            try
            {
                var key = await drafts.SaveDraftAsync(
                    new FormDefinitionId(formId), cid, provenance, bodyBytes, request.SubjectId, expiresAt: null, ct)
                    .ConfigureAwait(false);
                return Results.Ok(new DraftSavedResponse(key.Case.Value, key.Tenant.Value, key.PartyId.ToString("N")));
            }
            catch (PrincipalPartyResolutionException)
            {
                return Results.Json(new { code = "form_draft.party_unresolved" }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // GET — resume a draft.
        app.MapGet($"{RouteBase}/{{formId}}/drafts/{{caseId}}", async (
            string formId, string caseId, CancellationToken ct) =>
        {
            if (!DraftCaseId.TryCreate(caseId, out var cid))
            {
                return Results.BadRequest(new { code = "form_draft.case_id_malformed" });
            }

            using var scope = rootServices.CreateScope();
            var drafts = scope.ServiceProvider.GetRequiredService<ISubmissionDraftService>();

            try
            {
                var draft = await drafts.ResumeDraftAsync(cid, ct).ConfigureAwait(false);
                if (draft is null || !string.Equals(draft.FormId.Value, formId, StringComparison.Ordinal))
                {
                    return Results.NoContent(); // no resumable draft for this (case, party) on this form
                }
                return Results.Ok(DraftViewResponse.From(draft));
            }
            catch (PrincipalPartyResolutionException)
            {
                return Results.Json(new { code = "form_draft.party_unresolved" }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // DELETE — abandon a draft.
        app.MapDelete($"{RouteBase}/{{formId}}/drafts/{{caseId}}", async (
            string formId, string caseId, CancellationToken ct) =>
        {
            if (!DraftCaseId.TryCreate(caseId, out var cid))
            {
                return Results.BadRequest(new { code = "form_draft.case_id_malformed" });
            }

            using var scope = rootServices.CreateScope();
            var drafts = scope.ServiceProvider.GetRequiredService<ISubmissionDraftService>();

            try
            {
                var removed = await drafts.AbandonDraftAsync(cid, ct).ConfigureAwait(false);
                return removed ? Results.NoContent() : Results.NotFound();
            }
            catch (PrincipalPartyResolutionException)
            {
                return Results.Json(new { code = "form_draft.party_unresolved" }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // GET list — the operator's resumable drafts ("my in-progress cases").
        app.MapGet($"{RouteBase}/drafts", async (CancellationToken ct) =>
        {
            using var scope = rootServices.CreateScope();
            var drafts = scope.ServiceProvider.GetRequiredService<ISubmissionDraftService>();

            try
            {
                var mine = await drafts.ListMyDraftsAsync(ct).ConfigureAwait(false);
                var views = new List<DraftViewResponse>(mine.Count);
                foreach (var d in mine)
                {
                    views.Add(DraftViewResponse.From(d));
                }
                return Results.Ok(views);
            }
            catch (PrincipalPartyResolutionException)
            {
                return Results.Json(new { code = "form_draft.party_unresolved" }, statusCode: StatusCodes.Status403Forbidden);
            }
        });
    }
}

// ── Wire shapes (mirror @harborline-software/api-contracts forms draft DTOs) ─────────────────────

/// <summary>The PUT-draft request body: the partial candidate values + an optional data subject.</summary>
public sealed record DraftSaveRequest(
    [property: JsonPropertyName("values")] JsonElement Values,
    [property: JsonPropertyName("subjectId")] string? SubjectId);

/// <summary>The response after a successful draft save — echoes the resolved key parts.</summary>
public sealed record DraftSavedResponse(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("partyId")] string PartyId);

/// <summary>A resumed draft on the wire.</summary>
public sealed record DraftViewResponse(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("formId")] string FormId,
    [property: JsonPropertyName("values")] JsonElement Values,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt)
{
    /// <summary>Projects a <see cref="SubmissionDraft"/> onto the wire DTO (deserializing the stored body).</summary>
    public static DraftViewResponse From(SubmissionDraft draft)
    {
        using var doc = JsonDocument.Parse(draft.Body);
        return new DraftViewResponse(
            draft.Key.Case.Value,
            draft.FormId.Value,
            doc.RootElement.Clone(),
            draft.SubjectId,
            draft.UpdatedAt.ToString("O"));
    }
}
