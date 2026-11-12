using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.LayoutRuntime;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T-735: serves an Active installed pack's Layout surface for one root record through the registered
/// <see cref="LayoutSurfaceHost"/>, under the selected-session principal and the existing
/// <see cref="RequestAuthorization"/> pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opening a surface (DES-0052 layout-eng-26; T-724 ruling 76; owner ruling of 2026-11-04; MITRE CWE-862).</b>
/// <c>layout:open</c> on the requested surface is decided first, deny by default. It is the decision
/// <see cref="LayoutSurfaceHost"/> also makes: the same operation and record kind, on the identity the surface is
/// matched by. The route has to enforce it, because the platform resolver does not read that decision. No seeded
/// role or definition offers the operation yet, so until one is ruled every caller is refused here.
/// </para>
/// <para>
/// <b>Root existence (MITRE CWE-203; OWASP API Security Top 10 2023, API1).</b> <c>records:read</c> on the root is
/// decided next, still before the surface, the root or anything else is looked up. A caller who may not open the
/// surface gets one refusal whatever the root and surface are and whatever else they hold; a caller who may open
/// it but not read the root gets one refusal whether or not the root exists. Only a caller holding both learns
/// that a surface or a root is unknown.
/// </para>
/// <para>
/// <b>Related targets (DES-0052 layout-eng-31).</b> The relationship map comes from the Records reference fields the
/// root's Record Type declares in the surface pack's signed closure (layout-ck-28, layout-ck-39); the lookups run
/// inside the host's floor through its traversal callbacks, and that floor counts from the start of this handler. A missing and a denied target resolve to the same
/// absent block with the same status, so nothing on the wire says which it was. The response body carries no request
/// or correlation id; root-read receipts use audit headers. The denial trace is keyed by the caller's own
/// <c>X-Correlation-ID</c> when it sends one.
/// </para>
/// <para>
/// <b>The floor's end (T-1195; M11 ruling under T-724 ruling 93, control 1341).</b> Every served path writes its
/// root-read receipt and builds its response body inside the host's floor, in the same order, so only the send
/// follows the deadline. Work after the floor is the same on both paths but not free, and its jitter lands on the
/// measured time; inside the floor the deadline absorbs it.
/// </para>
/// </remarks>
public static class LayoutSurfaceRoutes
{
    /// <summary>The route: one installed surface, by its definition identity, for one root record.</summary>
    public const string RouteTemplate = "/api/local-node/layout/surfaces/{surfaceId}/records/{recordId}";

    /// <summary>The RFC 9457 problem media type every refusal this route owns is written with.</summary>
    internal const string ProblemContentType = "application/problem+json";

    /// <summary>The media type a served surface is written with, the one <see cref="Results.Json(object?, JsonSerializerOptions?, string?, int?)"/> wrote before T-1195.</summary>
    internal const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Maps the surface route, closing over dependencies from the outer host container.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        LayoutSurfaceHost host,
        IPackInstallStore packs,
        ILayoutRootRecordSource roots,
        ILayoutRelatedRecordSource related,
        TimeProvider time,
        AuthorizedActAudit audit)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(related);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(audit);

        app.MapGet(RouteTemplate, async (string surfaceId, string recordId, HttpContext http, CancellationToken ct) =>
        {
            // The related-target floor starts here. Every served path writes the same root-read receipt and builds its
            // body inside it (T-1195), so only the send follows the deadline.
            var started = time.GetTimestamp();
            http.Response.Headers.CacheControl = "no-store";
            var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
            if (principal is null || principal.TenantId.IsSystemSentinel)
                return Results.Unauthorized();
            if (SelectedRequestCorrelation.Bind(http) is { } invalidCorrelation)
                return invalidCorrelation;
            var tenant = principal.TenantId;

            // Both decided before any lookup, at one instant: existence is not probeable through a refusal.
            var authority = RequestAuthorization.Authority(http, tenant, time);
            var cannotOpen = await RequestAuthorization.RefusalAsync(
                    http, authority, LayoutSurfaceHost.LayoutOpen.Value, RouteRecord.Of(surfaceId), ct)
                .ConfigureAwait(false);
            if (cannotOpen is not null)
                return cannotOpen;
            AuthorizationDecision? accepted = null;
            var cannotRead = await RequestAuthorization.RefusalAsync(
                    http, authority, TeamRolePermissions.RecordsRead, RouteRecord.Of(recordId), ct,
                    decision => accepted = decision)
                .ConfigureAwait(false);
            if (cannotRead is not null)
                return cannotRead;

            var (surface, ambiguous) = InstalledLayoutSurfaces.Find(packs, tenant, surfaceId);
            if (ambiguous)
                return Problem(StatusCodes.Status409Conflict, LayoutSurfaceCodes.SurfaceAmbiguous,
                    "More than one installed pack carries this Layout surface.");
            if (surface is null)
                return Problem(StatusCodes.Status404NotFound, LayoutSurfaceCodes.SurfaceUnknown,
                    "No installed pack carries this Layout surface.");

            var root = await roots.FindAsync(tenant, recordId, ct).ConfigureAwait(false);
            if (root is null)
                return Problem(StatusCodes.Status404NotFound, LayoutSurfaceCodes.RootUnknown,
                    "The root record does not exist.");

            var rootType = InstalledLayoutSurfaces.RecordType(packs, tenant, surface.Pack, root.RecordTypeId);
            var relationships = LayoutRelationshipMap.Build(surface.Definition, rootType,
                new LayoutRelatedRecordRequestCache(related, tenant), out var refusal);
            if (refusal is not null)
                return Problem(StatusCodes.Status422UnprocessableEntity, refusal.Code,
                    "A related block's relationship is not a single-target reference the root's Record Type declares.",
                    refusal.Relationship);

            var requestId = (http.Features.Get<SelectedRequestCorrelation>()?.Value ?? Guid.NewGuid()).ToString("D");
            try
            {
                Guid? receipt = null;
                byte[]? body = null;
                var serializer = http.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value
                    .SerializerOptions ?? new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
                await host.ResolveAsync(surface.Definition, LayoutHostBindingSources.Instance,
                    relationships, LayoutBindingScope.Root(LayoutHostBindingSources.WithDeclaredFields(root.Values, rootType)),
                    tenant, NodeGatePrincipal.Of(principal), requestId, started, ct,
                    insideFloor: async (resolution, token) =>
                    {
                        receipt = await audit.RecordAsync(new AuditEventType("RecordRead"), accepted!,
                            new Dictionary<string, object?> { ["recordId"] = recordId }, token).ConfigureAwait(false);
                        body = JsonSerializer.SerializeToUtf8Bytes(LayoutSurfaceResponse.Of(surface.Definition, resolution), serializer);
                    }).ConfigureAwait(false);
                if (receipt is { } auditId)
                {
                    http.Response.Headers["X-Harborline-Audit-Id"] = auditId.ToString("D");
                    if (accepted!.Request.CorrelationId is { } correlation)
                        http.Response.Headers["X-Harborline-Audit-Correlation"] = correlation.ToString("D");
                }
                return Results.Bytes(body!, JsonContentType);
            }
            catch (LayoutAuditDegradedException)
            {
                return Problem(StatusCodes.Status503ServiceUnavailable, LayoutAuditDegradedException.Code,
                    "Related records are unavailable while this node's audit trail is degraded.");
            }
        });
    }

    private static IResult Problem(int status, string code, string title, string? relationship = null)
        => Results.Json(new LayoutSurfaceProblem(title, status, code, relationship),
            contentType: ProblemContentType, statusCode: status);
}

/// <summary>An RFC 9457 problem with the route's stable <paramref name="Code"/> as an extension member.</summary>
/// <param name="Title">A short, fixed summary of the problem type.</param>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Code">The stable refusal code.</param>
/// <param name="Relationship">For a relationship refusal, the authored key that did not bind.</param>
public sealed record LayoutSurfaceProblem(
    string Title,
    int Status,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Relationship = null);

/// <summary>A resolved surface on the wire: what placed, what a guard withheld and what refused.</summary>
/// <param name="Surface">The surface's definition identity.</param>
/// <param name="Version">The installed definition version.</param>
/// <param name="Blocks">The placed blocks, in authored order.</param>
/// <param name="Refusals">One entry per unresolvable binding.</param>
/// <param name="Hidden">The blocks a <c>show_when</c> guard withheld.</param>
public sealed record LayoutSurfaceResponse(
    string Surface,
    string Version,
    IReadOnlyList<LayoutSurfaceBlock> Blocks,
    IReadOnlyList<LayoutSurfaceRefusal> Refusals,
    IReadOnlyList<string> Hidden)
{
    internal static LayoutSurfaceResponse Of(Harborline.Blocks.BuilderDefinitions.LayoutDefinition definition,
        LayoutBindingResolution resolution) => new(
        definition.Envelope.Identity,
        definition.Envelope.Version,
        [.. resolution.Blocks.Select(block => new LayoutSurfaceBlock(
            block.BlockId, block.Kind, block.BindingKind, block.Name, block.Value?.DeepClone(), block.RowId))],
        [.. resolution.Refusals.Select(refusal => new LayoutSurfaceRefusal(
            refusal.BlockId, refusal.BindingKind, refusal.Name, refusal.Code, refusal.RowId))],
        [.. resolution.Hidden]);
}

/// <summary>One placed block.</summary>
public sealed record LayoutSurfaceBlock(
    string BlockId,
    string Kind,
    string BindingKind,
    string Name,
    JsonNode? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RowId);

/// <summary>One unresolvable binding.</summary>
public sealed record LayoutSurfaceRefusal(
    string BlockId,
    string BindingKind,
    string Name,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RowId);
