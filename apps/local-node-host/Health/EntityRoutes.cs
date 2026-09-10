using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 gap C4 — node-local <c>legal_entities</c> route mapping.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/entities</c> — list all legal entities in the
///     embedded node's single-device store.</item>
///   <item><c>POST /api/local-node/entities</c> — create the first (or any subsequent)
///     legal entity offline; persists directly to the SQLCipher-encrypted
///     <see cref="LocalNodeDbContext"/> (<c>legal_entities</c> table). No Bridge
///     required. Server-assigns a new <see cref="LegalEntityId"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant scoping.</b> All entities are written under the active-team-derived
/// <see cref="TenantId"/> the route resolves via <see cref="NodeTenant"/>
/// (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer) so the tenant-keyed
/// <see cref="LegalEntity"/> contract (ADR 0104 §7, <c>IMustHaveTenant</c>) is satisfied;
/// the tenant follows the active org rather than a fixed <c>"local"</c> sentinel, so
/// switching the active org switches which org's entities a route reads/writes.
/// </para>
/// <para>
/// <b>Tenant-isolation note.</b> Reads/writes filter on the active-team tenant — the
/// explicit predicate is the per-org isolation boundary (cross-org leakage is prevented
/// by construction once the active org switches the tenant; ADR 0032), no longer relying
/// on a single install-constant <c>"local"</c> value.
/// </para>
/// <para>
/// The factory is injected from the OUTER host container (same pattern as
/// <see cref="HostedMaintenanceApiEndpoint"/> and
/// <see cref="HostedLocalNodeApiEndpoint"/>). Do NOT use <c>[FromServices]</c>:
/// the routes are mapped on <see cref="SharedHostedWebApp"/>'s inner
/// <see cref="Microsoft.AspNetCore.Builder.WebApplication"/> whose service
/// provider is a SEPARATE container (bug-2849 trap).
/// </para>
/// </remarks>
public static class EntityRoutes
{
    /// <summary>Canonical route base for the node-local entity surface.</summary>
    public const string RouteBase = "/api/local-node/entities";

    /// <summary>
    /// Stable single-device tenant sentinel. The embedded node serves exactly
    /// one operator — all entities are scoped to this tenant value so the
    /// <see cref="LegalEntity.TenantId"/> contract is satisfied without a
    /// session stack.

    /// <summary>
    /// The schema identity the pre-commit <see cref="IEntityValidator"/> hook receives for a
    /// legal-entity body (ticket 151 — the write pipeline's authority-side validation stage).
    /// </summary>
    public static readonly SchemaId LegalEntitySchema = new("legal-entity");

    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps the entity routes onto <paramref name="app"/>, closing over the
    /// <see cref="LocalNodeDbContext"/> <paramref name="factory"/> from the outer
    /// host container. <paramref name="validator"/> is the registered pre-commit
    /// <see cref="IEntityValidator"/>; when omitted the registered container default applies
    /// (ticket 151: the POST now RUNS the validator instead of structurally bypassing it).
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        IDbContextFactory<LocalNodeDbContext> factory,
        IActiveTeamAccessor activeTeam,
        NodeEntityWriter writer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // GET /api/local-node/entities — list all entities for the local tenant.
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var rows = await ctx.Set<LegalEntity>()
                .AsNoTracking()
                .Where(e => e.TenantId == LocalTenantId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(new EntityListResponse(
                rows.Select(EntityItemDto.From).ToList()));
        });

        // POST /api/local-node/entities — create a legal entity offline.
        app.MapPost(RouteBase, async (
            CreateEntityRequest body,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Ticket 151 stage-one gate: an authorization decision precedes any persistence work —
            // the same request-scoped mechanism the gated sibling routes use (ContactRoutes /
            // InvoiceRoutes). Legal entities are operational records → records:write.
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            // One act, one clock read: the authority the create is STAMPED with is the one the guard
            // decides on (ticket 216; the invoice and journal-entry write routes do the same).
            var authority = new AuthorizationWriteContext(
                new ActorId(NodeCallerParty.Resolve(http).Value),
                LocalTenantId,
                timeProvider.GetUtcNow());
            // The created entity has no id yet, so this act addresses the install, not a record.
            if (await RequestAuthorization.RefusalAsync(
                    http, authority, TeamRolePermissions.RecordsWrite, RouteRecord.TheInstall, ct)
                is { } denied)
                return denied;

            var entityId = LegalEntityId.NewId();

            LegalEntity entity;
            try
            {
                entity = await writer.CreateLegalEntityAsync(
                    new CreateLegalEntityCommand(
                        entityId,
                        body?.LegalName,
                        body?.Kind,
                        body?.TaxClassification,
                        body?.CommonControlGroupId),
                    authority,
                    ct).ConfigureAwait(false);
            }
            catch (EntityValidationException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    error = "validation_failed",
                    code = ex.ReasonCode,
                    pointers = ex.Pointers,
                    detail = ex.Message,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            return Results.Created(
                $"{RouteBase}/{Uri.EscapeDataString(entity.Id.Value)}",
                new EntityCreatedResponse(entity.Id.Value, entity.LegalName));
        });
    }
}

// ── Wire shapes ───────────────────────────────────────────────────────────────

/// <summary>List response envelope matching the Bridge <c>/api/v1/entities</c> shape.</summary>
public sealed record EntityListResponse(
    [property: JsonPropertyName("entities")] IReadOnlyList<EntityItemDto> Entities);

/// <summary>Single entity item in the list response.</summary>
public sealed record EntityItemDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("legalName")] string LegalName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("taxClassification")] string TaxClassification,
    [property: JsonPropertyName("commonControlGroupId")] string? CommonControlGroupId)
{
    /// <summary>Projects a <see cref="LegalEntity"/> domain record onto the wire DTO.</summary>
    public static EntityItemDto From(LegalEntity e) => new(
        e.Id.Value,
        e.LegalName,
        e.Kind.ToString(),
        e.TaxClassification.ToString(),
        e.CommonControlGroupId);
}

/// <summary>201 Created response after a successful entity creation.</summary>
public sealed record EntityCreatedResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("legalName")] string LegalName);

/// <summary>Request body for <c>POST /api/local-node/entities</c>.</summary>
public sealed record CreateEntityRequest(
    [property: JsonPropertyName("legalName")] string LegalName,
    [property: JsonPropertyName("kind")] string Kind = "Llc",
    [property: JsonPropertyName("taxClassification")] string TaxClassification = "DisregardedEntity",
    [property: JsonPropertyName("commonControlGroupId")] string? CommonControlGroupId = null);
