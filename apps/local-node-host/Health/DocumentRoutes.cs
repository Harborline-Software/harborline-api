using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Docs.Models;
using Harborline.Api.Blocks.Docs.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Docs;
using Harborline.Api.LocalNodeHost.Data.Financial;
using DocumentServices = (
    Harborline.Api.Blocks.Docs.Services.IAttachmentRepository Attachments,
    Harborline.Api.Blocks.Docs.Services.IAttachmentService AttachmentService,
    Harborline.Api.Blocks.Docs.Services.IDocumentRefService DocumentRefs);

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local documents surface — the T4 documents node-flip (ADR 0127). Documents
/// (the <c>blocks-docs</c> cluster — <see cref="Attachment"/> + <see cref="DocumentRef"/>)
/// flip node-local: file bytes live INLINE in the SQLCipher-keyed <c>local-node.db</c>
/// (<c>StorageRef.ForInline</c> → base64 inside <c>storage_ref_json</c>), so list /
/// detail / upload / content / attach all work with signal-bridge STOPPED.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes</b> (mirror the Bridge <c>DocumentsEndpoints</c> wire shapes so the
/// desktop client rebind is a path swap — <c>/api/v1/documents</c> →
/// <c>/api/local-node/documents</c>):
/// <list type="bullet">
///   <item><c>GET /api/local-node/documents</c> — list live attachments (tenant-wide).</item>
///   <item><c>GET /api/local-node/documents/{id}</c> — attachment detail; opaque 404 if absent / other-tenant.</item>
///   <item><c>GET /api/local-node/documents/{id}/content</c> — the raw inline bytes (Content-Type = sniffed MIME). Backs the page's inline image preview.</item>
///   <item><c>POST /api/local-node/documents</c> — multipart upload (sniff → sanitize → CONCRETE inline-ceiling policy). 201 Created.</item>
///   <item><c>POST /api/local-node/documents/{id}/attach</c> — idempotent cross-cluster link (Attachment → parent entity).</item>
/// </list>
/// </para>
/// <para>
/// <b>SEC-1 (ADR 0127, BUILD-BLOCKING).</b> The upload goes through
/// <see cref="IAttachmentService.UploadAsync"/>, which runs the CONCRETE
/// <see cref="IMimeTypeAndSizePolicy"/> wired by
/// <see cref="NodeDocsWriteComposition.AddNodeDocsWrites"/> — the shared three-gate
/// policy + the 25&#160;MB inline ceiling (<see cref="NodeInlineCeilingPolicy"/>). A
/// null/unconfigured policy is structurally impossible (the composition builds the
/// service WITH the policy + throws on a non-positive ceiling). An above-ceiling
/// upload is REJECTED with an actionable, PII-free error
/// (<see cref="UploadRejectedException"/> → 413).
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Documents are TENANT-WIDE (no entity
/// dimension); every read + write resolves the active-team-derived tenant via
/// <see cref="NodeTenant"/> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer),
/// not a fixed <c>"local"</c> sentinel. No <c>X-Sunfish-Entity</c> header (the Bridge
/// client sent none; nothing to preserve/drop). The explicit tenant predicates in the
/// repos are the per-org isolation boundary — switching the active org switches the
/// documents a query reads.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): they bind to the loopback-only Kestrel
/// listener (ADR 0127 OQ3 = DROP CSRF; the same posture as every other
/// <c>/api/local-node/*</c> route). The Bridge endpoint keeps CSRF (network-reachable).
/// </para>
/// <para>
/// <b>Audit-envelope durable-layer pattern (ADR 0104 §7).</b> No inline signed audit
/// event — the node IS the durable mutation layer; the row's presence in the keyed
/// SQLCipher store satisfies X-AUDIT (the same posture the node JE / bill stores carry).
/// </para>
/// <para>
/// <b>Wiring.</b> The <see cref="DocumentServices"/> is injected from the OUTER host
/// container and passed to <see cref="Map"/> as a closed-over dependency — NOT
/// resolved via <c>[FromServices]</c>, which would fail on the inner shared-app
/// container (bug-2849).
/// </para>
/// </remarks>
public static class DocumentRoutes
{
    /// <summary>Canonical route base for the node-local documents surface.</summary>
    public const string RouteBase = "/api/local-node/documents";

    /// <summary>
    /// Maps the document routes onto <paramref name="app"/>, closing over the
    /// <paramref name="docs"/> accessor (node EF repos + upload + link services) from
    /// the outer host container.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, DocumentServices docs, IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(activeTeam);

        MapList(app, docs, activeTeam);
        MapDetail(app, docs, activeTeam);
        MapContent(app, docs, activeTeam);
        MapUpload(app, docs, activeTeam);
        MapAttach(app, docs, activeTeam);
    }

    // ── GET /api/local-node/documents — list live attachments (tenant-wide) ──────
    private static void MapList(IEndpointRouteBuilder app, DocumentServices docs, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var items = await docs.Attachments.ListByTenantAsync(LocalTenantId, ct).ConfigureAwait(false);
            // Newest-first (small single-device set; in-memory sort mirrors the other node lists).
            var ordered = items.OrderByDescending(a => a.CreatedAtUtc.Value);
            return Results.Ok(new DocumentListWire(
                Documents: ordered.Select(DocumentSummaryWire.From).ToList()));
        });
    }

    // ── GET /api/local-node/documents/{id} — detail; opaque 404 ──────────────────
    private static void MapDetail(IEndpointRouteBuilder app, DocumentServices docs, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{id}}", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var attachment = await docs.Attachments.GetAsync(new AttachmentId(id), ct).ConfigureAwait(false);
            if (attachment is null || !attachment.TenantId.Equals(LocalTenantId))
            {
                // Uniform 404 — absent / tombstoned / cross-tenant all read the same (ADR 0092 §A3).
                return Results.NotFound();
            }
            return Results.Ok(DocumentDetailWire.From(attachment));
        });
    }

    // ── GET /api/local-node/documents/{id}/content — raw inline bytes ────────────
    private static void MapContent(IEndpointRouteBuilder app, DocumentServices docs, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/{{id}}/content", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var attachment = await docs.Attachments.GetAsync(new AttachmentId(id), ct).ConfigureAwait(false);
            if (attachment is null || !attachment.TenantId.Equals(LocalTenantId))
            {
                return Results.NotFound();
            }

            // v1 stores bytes inline; the deferred out-of-line tiers don't exist yet.
            // A non-inline ref (only reachable if a future tier mis-routes) is a 404.
            if (attachment.StorageRef.Kind != StorageRefKind.Inline
                || attachment.StorageRef.InlineBytes is not { } inline)
            {
                return Results.NotFound();
            }

            // Serve the sniffed MIME (persisted at upload; the filename ext is not trusted).
            // SVG SECURITY: the frontend only renders raster <img> + PDF inline; SVG is
            // download-only there. We still return the bytes with their sniffed type; the
            // browser does not execute an <img>-loaded resource as a document.
            return Results.File(inline.ToArray(), attachment.MimeType, attachment.OriginalFilename);
        });
    }

    // ── POST /api/local-node/documents — multipart upload (no CSRF; loopback) ────
    private static void MapUpload(IEndpointRouteBuilder app, DocumentServices docs, IActiveTeamAccessor activeTeam)
    {
        app.MapPost(RouteBase, async (HttpRequest request, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "multipart_required" });
            }

            IFormFile? file;
            string sensitivityRaw;
            try
            {
                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                file = form.Files.GetFile("file");
                sensitivityRaw = form["sensitivity"].ToString();
            }
            catch (Exception)
            {
                return Results.BadRequest(new { error = "form_read_error" });
            }

            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "file_required" });
            }

            var sensitivity = Sensitivity.Internal;
            if (!string.IsNullOrWhiteSpace(sensitivityRaw)
                && !Enum.TryParse(sensitivityRaw, ignoreCase: true, out sensitivity))
            {
                return Results.BadRequest(new { error = "invalid_sensitivity" });
            }

            byte[] bytes;
            try
            {
                using var ms = new MemoryStream(file.Length <= int.MaxValue ? (int)file.Length : 0);
                await file.CopyToAsync(ms, ct).ConfigureAwait(false);
                bytes = ms.ToArray();
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            var originalFilename = file.FileName ?? "attachment.bin";

            Attachment attachment;
            try
            {
                // SEC-1: UploadAsync runs the CONCRETE policy internally (shared three
                // gates + the 25 MB inline ceiling). UploadRejectedException on any gate.
                attachment = await docs.AttachmentService.UploadAsync(
                    tenantId:          LocalTenantId,
                    bytes:             bytes.AsMemory(),
                    mimeType:          file.ContentType ?? "application/octet-stream",
                    originalFilename:  originalFilename,
                    createdBy:         NodeCallerParty.Resolve(request.HttpContext).Value,
                    sensitivity:       sensitivity,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (UploadRejectedException ex)
            {
                // Map the rejection reason → HTTP. InlineSize (above-ceiling) + Size →
                // 413 Payload Too Large; Mime → 415; TenantQuota → 507. The Detail is
                // already PII-scrubbed + actionable (AttachmentService logs the
                // structured rejection per SE-4). The detail flows to the client so the
                // 25 MB message is surfaced, not a bare status.
                var status = ex.RejectionReason switch
                {
                    PolicyRejection.InlineSize  => StatusCodes.Status413PayloadTooLarge,
                    PolicyRejection.Size        => StatusCodes.Status413PayloadTooLarge,
                    PolicyRejection.Mime        => StatusCodes.Status415UnsupportedMediaType,
                    PolicyRejection.TenantQuota => StatusCodes.Status507InsufficientStorage,
                    _                           => StatusCodes.Status422UnprocessableEntity,
                };
                return Results.Json(
                    new { error = "upload_rejected", reason = ex.RejectionReason.ToString(), detail = ex.Detail },
                    statusCode: status);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.Created(
                $"{RouteBase}/{attachment.Id.Value}",
                DocumentDetailWire.From(attachment));
        });
    }

    // ── POST /api/local-node/documents/{id}/attach — idempotent link (no CSRF) ───
    private static void MapAttach(IEndpointRouteBuilder app, DocumentServices docs, IActiveTeamAccessor activeTeam)
    {
        app.MapPost($"{RouteBase}/{{id}}/attach", async (
            string id,
            AttachDocumentWire? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (body is null
                || string.IsNullOrWhiteSpace(body.ClusterCode)
                || string.IsNullOrWhiteSpace(body.ParentEntityType)
                || string.IsNullOrWhiteSpace(body.ParentEntityId))
            {
                return Results.BadRequest(new { error = "attach_fields_required" });
            }

            var attachmentId = new AttachmentId(id);

            // Existence + tenant check (uniform 404 on absent / cross-tenant).
            var attachment = await docs.Attachments.GetAsync(attachmentId, ct).ConfigureAwait(false);
            if (attachment is null || !attachment.TenantId.Equals(LocalTenantId))
            {
                return Results.NotFound();
            }

            DocumentRef docRef;
            try
            {
                // Idempotent on (tenant, attachment, cluster, parent-type, parent-id).
                docRef = await docs.DocumentRefs.LinkAsync(
                    tenantId:          LocalTenantId,
                    attachmentId:      attachmentId,
                    clusterCode:       body.ClusterCode!,
                    parentEntityType:  body.ParentEntityType!,
                    parentEntityId:    body.ParentEntityId!,
                    actor:             NodeCallerParty.Resolve(http).Value,
                    attachmentRole:    body.AttachmentRole,
                    cancellationToken: ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Cross-tenant / tombstoned attachment — the caller's reference is invalid.
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Results.Created(
                $"{RouteBase}/{id}/attach",
                DocumentRefWire.From(docRef));
        });
    }
}

// ── Wire shapes (mirror the Bridge DocumentsEndpoints DTOs; camelCase JSON) ──────

/// <summary>Summary row in the node document list response.</summary>
public sealed record DocumentSummaryWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("originalFilename")] string OriginalFilename,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("createdBy")] string? CreatedBy)
{
    /// <summary>Projects an <see cref="Attachment"/> onto the summary wire shape (status mapped to the Bridge's Live/Superseded/Deleted vocabulary).</summary>
    public static DocumentSummaryWire From(Attachment a) => new(
        Id:               a.Id.Value,
        OriginalFilename: a.OriginalFilename,
        MimeType:         a.MimeType,
        SizeBytes:        a.SizeBytes,
        Sensitivity:      a.Sensitivity.ToString(),
        Status:           StatusWire(a.Status),
        CreatedAt:        a.CreatedAtUtc.Value.ToString("O"),
        CreatedBy:        a.CreatedBy);

    /// <summary>
    /// Map the domain <see cref="AttachmentStatus"/> to the Bridge frontend's
    /// <c>DocumentStatus</c> vocabulary (Live / Superseded / Deleted) so the
    /// rebound client renders identically.
    /// </summary>
    internal static string StatusWire(AttachmentStatus s) => s switch
    {
        AttachmentStatus.Active     => "Live",
        AttachmentStatus.Superseded => "Superseded",
        AttachmentStatus.Tombstoned => "Deleted",
        _                           => s.ToString(),
    };
}

/// <summary>Response envelope for <c>GET /api/local-node/documents</c>: <c>{ "documents": [...] }</c>.</summary>
public sealed record DocumentListWire(
    [property: JsonPropertyName("documents")] IReadOnlyList<DocumentSummaryWire> Documents);

/// <summary>Detail wire shape (mirrors the Bridge <c>DocumentDetailDto</c>) — returned by detail + upload.</summary>
public sealed record DocumentDetailWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("originalFilename")] string OriginalFilename,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("replacesAttachmentId")] string? ReplacesAttachmentId,
    [property: JsonPropertyName("replacedByAttachmentId")] string? ReplacedByAttachmentId,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("createdBy")] string? CreatedBy,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt)
{
    /// <summary>Projects an <see cref="Attachment"/> onto the detail wire shape.</summary>
    public static DocumentDetailWire From(Attachment a) => new(
        Id:                     a.Id.Value,
        OriginalFilename:       a.OriginalFilename,
        MimeType:               a.MimeType,
        SizeBytes:              a.SizeBytes,
        ContentHash:            a.ContentHash,
        Sensitivity:            a.Sensitivity.ToString(),
        Status:                 DocumentSummaryWire.StatusWire(a.Status),
        ReplacesAttachmentId:   a.ReplacesAttachmentId?.Value,
        ReplacedByAttachmentId: a.ReplacedByAttachmentId?.Value,
        CreatedAt:              a.CreatedAtUtc.Value.ToString("O"),
        CreatedBy:              a.CreatedBy,
        UpdatedAt:              a.UpdatedAtUtc.Value.ToString("O"));
}

/// <summary>POST body for <c>POST /api/local-node/documents/{id}/attach</c>.</summary>
public sealed record AttachDocumentWire(
    [property: JsonPropertyName("clusterCode")] string ClusterCode,
    [property: JsonPropertyName("parentEntityType")] string ParentEntityType,
    [property: JsonPropertyName("parentEntityId")] string ParentEntityId,
    [property: JsonPropertyName("attachmentRole")] string? AttachmentRole = null);

/// <summary>Response envelope for the attach route (mirrors the Bridge <c>DocumentRefDto</c>).</summary>
public sealed record DocumentRefWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("attachmentId")] string AttachmentId,
    [property: JsonPropertyName("clusterCode")] string ClusterCode,
    [property: JsonPropertyName("parentEntityType")] string ParentEntityType,
    [property: JsonPropertyName("parentEntityId")] string ParentEntityId,
    [property: JsonPropertyName("attachmentRole")] string? AttachmentRole,
    [property: JsonPropertyName("createdAt")] string CreatedAt)
{
    /// <summary>Projects a <see cref="DocumentRef"/> onto the wire shape.</summary>
    public static DocumentRefWire From(DocumentRef r) => new(
        Id:               r.Id.Value,
        AttachmentId:     r.AttachmentId.Value,
        ClusterCode:      r.ClusterCode,
        ParentEntityType: r.ParentEntityType,
        ParentEntityId:   r.ParentEntityId,
        AttachmentRole:   r.AttachmentRole,
        CreatedAt:        r.CreatedAtUtc.Value.ToString("O"));
}
