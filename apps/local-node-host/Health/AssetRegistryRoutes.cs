using System.Globalization;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Definitions.Compatibility;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local Asset Type System surface (ADR 0101 Rev 3.1 Wave 2b) — the read + capture routes the
/// Harborline App Workshop UI binds to. Browses the typed-entity registry, its containment tree (as-of), and
/// each entity's condition history; creates entities of a type and adds containment / located-at edges.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant scoping.</b> Every read + write resolves the active-team tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (not a fixed sentinel); the registry stores fail closed on the
/// system tenant and never return another tenant's rows, so a cross-tenant id is an opaque 404.
/// </para>
/// <para>
/// <b>As-of clock (A5c).</b> Tree + condition reads take an explicit <c>?asOf=</c> ISO-8601 instant;
/// absent, they use the node clock's now. Nothing here reads an ambient <c>DateTime.Now</c> — the
/// clock is the injected <see cref="TimeProvider"/>, and every mutation records at that instant.
/// </para>
/// <para>
/// <b>Live capture.</b> Condition assessments are NOT written here directly — they are the "one act,
/// two artifacts" side record the condition-rating inspection form writes through the
/// <c>ProjectingFormEngine</c> on the forms submit route (wired by <c>AddNodeAssetRegistry</c>). This
/// surface only READS the resulting condition history; the write path is the governed, audited,
/// gate-protected projection.
/// </para>
/// <para>
/// <b>Wiring (bug-2849).</b> Store dependencies are injected from the OUTER host container and closed
/// over by <see cref="Map"/> — never resolved via <c>[FromServices]</c> inside the handlers.
/// </para>
/// </remarks>
public static class AssetRegistryRoutes
{
    /// <summary>Canonical route base for the node-local asset-registry surface.</summary>
    public const string RouteBase = "/api/local-node/asset-registry";

    /// <summary>Maps the asset-registry routes, closing over the registry stores + active-team accessor + clock.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IEntityTypeRegistry types,
        IRegistryEntityRepository entities,
        ITypedRelationshipStore edges,
        IConditionAssessmentStore conditions,
        IFormSubmissionRecordStore submissions,
        IActiveTeamAccessor activeTeam,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(submissions);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(clock);

        MapTypes(app, types, activeTeam);
        MapTypeManagement(app, types, activeTeam, clock);
        MapEntities(app, types, entities, edges, activeTeam, clock);
        MapTree(app, entities, edges, activeTeam, clock);
        MapCondition(app, entities, conditions, activeTeam, clock);
        MapSubmissions(app, entities, submissions, activeTeam);
        MapEdges(app, edges, activeTeam, clock);
    }

    // ── GET /types — effective type catalog (tenant rows override shared seeds) ──────
    private static void MapTypes(IEndpointRouteBuilder app, IEntityTypeRegistry types, IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/types", async (CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var effective = new Dictionary<string, TypeWire>(StringComparer.Ordinal);
            foreach (var seed in types.ListSeeds())
                effective[seed.Id.Value] = ToTypeWire(seed.Id, seed.Descriptor, seed.Provenance.ToString(), overridesSeed: false);
            foreach (var t in await types.ListTypesAsync(tenant, ct).ConfigureAwait(false))
                effective[t.Id.Value] = ToTypeWire(t.Id, t.Descriptor, t.Provenance.ToString(), overridesSeed: t.OverrideOf is not null);

            return Results.Ok(new TypeListResponse(effective.Values.OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray()));
        });
    }

    // ── Type management (the Type Manager editor) ─────────────────────────────────────
    // GET /types/{id}  ·  POST /types  ·  PUT /types/{id}  ·  POST /types/{id}/revert
    //
    // Seed/override invariant (F2 invariant 1): editing a SEEDED type never mutates the shared seed —
    // it writes a tenant-scoped OVERRIDE row. A tenant's own greenfield type is updated in place.
    // Revert discards a tenant override so the shared seed is once again effective. All tenant-scoped
    // via NodeTenant.Resolve; every write rides the audited registry at the node clock's instant.
    private static void MapTypeManagement(
        IEndpointRouteBuilder app, IEntityTypeRegistry types, IActiveTeamAccessor activeTeam, TimeProvider clock)
    {
        // GET /types/{id} — the full editable detail of the effective type (tenant row overrides seed).
        app.MapGet($"{RouteBase}/types/{{id}}", async (string id, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var typeId = new EntityTypeId(id);
            var tenantRow = await types.GetTypeAsync(tenant, typeId, ct).ConfigureAwait(false);
            var seed = types.GetSeed(typeId);
            if (tenantRow is null && seed is null)
                return Results.NotFound();

            var (descriptor, provenance, overridesSeed) = tenantRow is not null
                ? (tenantRow.Descriptor, tenantRow.Provenance.ToString(), tenantRow.OverrideOf is not null)
                : (seed!.Descriptor, seed.Provenance.ToString(), false);

            return Results.Ok(ToTypeDetailWire(typeId, descriptor, provenance, overridesSeed,
                seedExists: seed is not null, hasTenantRow: tenantRow is not null));
        });

        // POST /types — create a tenant's own greenfield type (never an override; use PUT to edit a seed).
        app.MapPost($"{RouteBase}/types", async (TypeUpsertBody body, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            if (body is null || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "display_name_required" });

            // The disposition is validated whenever present (symmetry with PUT); on create there is
            // nothing to narrow, so a valid one is simply not consulted.
            if (!TryParseDisposition(body.Disposition, out _, out var dispositionError))
                return Results.BadRequest(new { error = dispositionError });

            var typeId = string.IsNullOrWhiteSpace(body.Id) ? EntityTypeId.NewId() : new EntityTypeId(body.Id.Trim());

            // Create is create: a seed or an existing tenant row with this id must be edited via PUT.
            if (types.GetSeed(typeId) is not null)
                return Results.Conflict(new { error = "seed_exists_use_put" });
            if (await types.GetTypeAsync(tenant, typeId, ct).ConfigureAwait(false) is not null)
                return Results.Conflict(new { error = "type_exists_use_put" });

            if (!TryBuildDescriptor(body, out var descriptor, out var descriptorError))
                return Results.BadRequest(new { error = descriptorError });

            var at = new Instant(clock.GetUtcNow());
            try
            {
                var created = await types.CreateTypeAsync(
                    new EntityType { Id = typeId, TenantId = tenant, Descriptor = descriptor, Provenance = CascadeLayer.Tenant },
                    at, NodeCallerParty.Resolve(http).Value, ct).ConfigureAwait(false);
                return Results.Created($"{RouteBase}/types/{created.Id.Value}",
                    ToTypeDetailWire(created.Id, created.Descriptor, created.Provenance.ToString(),
                        overridesSeed: false, seedExists: false, hasTenantRow: true));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "invalid_type" });
            }
        });

        // PUT /types/{id} — edit. A SEEDED type edit produces/updates the tenant OVERRIDE (seed
        // untouched); a tenant greenfield type is updated in place. Never mutates a shared seed.
        app.MapPut($"{RouteBase}/types/{{id}}", async (string id, TypeUpsertBody body, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            if (body is null || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "display_name_required" });
            if (!TryBuildDescriptor(body, out var descriptor, out var descriptorError))
                return Results.BadRequest(new { error = descriptorError });

            var typeId = new EntityTypeId(id);
            var seed = types.GetSeed(typeId);
            var existing = await types.GetTypeAsync(tenant, typeId, ct).ConfigureAwait(false);

            // Ticket 155 — the loss-confirmation gate, live on the edit route. PUT is full-replacement,
            // so an omitted field IS a removal (never a silent null overwrite): any change that removes
            // or narrows the current effective descriptor is refused without an explicit disposition
            // from the DefinitionPublisher vocabulary (migrate | flag | accept). Additions and value
            // edits stay one-click; creation (POST) is untouched.
            var current = existing?.Descriptor ?? seed?.Descriptor;
            if (current is null)
                return Results.NotFound();
            if (!TryParseDisposition(body.Disposition, out var disposition, out var dispositionError))
                return Results.BadRequest(new { error = dispositionError });
            var losses = ClassifyLosses(current, descriptor);
            if (losses.Count > 0 && disposition is null)
                return Results.Conflict(new { error = "narrowing_requires_disposition", losses });

            // Ticket 155 (parked): the disposition is ECHOED, not executed — no migration or flagging
            // happens yet. The echo makes the operator's choice and what it covered client-visible.
            var dispositioned = losses.Count > 0 && disposition is { } kind
                ? new DispositionedWire(kind.ToString().ToLowerInvariant(), losses.ToArray())
                : null;

            var at = new Instant(clock.GetUtcNow());

            try
            {
                if (seed is not null)
                {
                    // Editing a seeded type ALWAYS writes a tenant-scoped override — the seed is preserved.
                    var overrideRow = await types
                        .OverrideSeedAsync(tenant, typeId, descriptor, at, CascadeLayer.Tenant, NodeCallerParty.Resolve(http).Value, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(ToTypeDetailWire(overrideRow.Id, overrideRow.Descriptor,
                        overrideRow.Provenance.ToString(), overridesSeed: true, seedExists: true, hasTenantRow: true,
                        dispositioned));
                }

                if (existing is not null)
                {
                    // A tenant's own greenfield type — update in place (upsert), preserving provenance.
                    var updated = await types.CreateTypeAsync(
                        new EntityType
                        {
                            Id = typeId,
                            TenantId = tenant,
                            Descriptor = descriptor,
                            Provenance = existing.Provenance,
                            OverrideOf = existing.OverrideOf,
                        },
                        at, NodeCallerParty.Resolve(http).Value, ct).ConfigureAwait(false);
                    return Results.Ok(ToTypeDetailWire(updated.Id, updated.Descriptor, updated.Provenance.ToString(),
                        overridesSeed: updated.OverrideOf is not null, seedExists: false, hasTenantRow: true,
                        dispositioned));
                }

                return Results.NotFound();
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "invalid_type" });
            }
        });

        // POST /types/{id}/revert — discard the tenant override so the shared seed is effective again.
        // Reverting is an explicit discard-my-override action, so the ticket-155 loss gate deliberately
        // does NOT run here even though override-added bindings are dropped; revert's own loss
        // affordance is ticket 111's scope.
        app.MapPost($"{RouteBase}/types/{{id}}/revert", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var typeId = new EntityTypeId(id);
            var seed = types.GetSeed(typeId);
            if (seed is null)
                return Results.NotFound();

            try
            {
                await types.RevertOverrideAsync(tenant, typeId, new Instant(clock.GetUtcNow()), NodeCallerParty.Resolve(http).Value, ct)
                    .ConfigureAwait(false);
                // The effective type is now the shared seed (whether an override was removed or it was a no-op).
                return Results.Ok(ToTypeDetailWire(typeId, seed.Descriptor, seed.Provenance.ToString(),
                    overridesSeed: false, seedExists: true, hasTenantRow: false));
            }
            catch (InvalidOperationException)
            {
                // A greenfield type sharing the seed id is not an override → static refusal (no reflection).
                return Results.BadRequest(new { error = "not_a_seed_override" });
            }
        });
    }

    // ── GET /entities[?type=]  ·  GET /entities/{id}  ·  POST /entities ──────────────
    private static void MapEntities(
        IEndpointRouteBuilder app, IEntityTypeRegistry types, IRegistryEntityRepository entities,
        ITypedRelationshipStore edges, IActiveTeamAccessor activeTeam, TimeProvider clock)
    {
        app.MapGet($"{RouteBase}/entities", async (string? type, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            // ADR 0060 (ticket 358): authority travels with the READ. The list's act is over the install's
            // collection rather than any one row, so it carries RouteRecord.TheInstall and rides the
            // install-wide declaration on records:read; the decision is resolved BEFORE the repository is
            // touched, so an unauthorized caller learns nothing about what the tenant holds.
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, TeamRolePermissions.RecordsRead, RouteRecord.TheInstall, ct)
                .ConfigureAwait(false) is { } denied)
                return denied;
            var rows = type is { Length: > 0 }
                ? await entities.ListByTypeAsync(tenant, new EntityTypeId(type), false, ct).ConfigureAwait(false)
                : await entities.ListByTenantAsync(tenant, false, ct).ConfigureAwait(false);
            return Results.Ok(new EntityListResponse(rows.Select(ToEntityWire).ToArray()));
        });

        app.MapGet($"{RouteBase}/entities/{{id}}", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            // The detail read names the record it addresses, so a grant scoped to another entity refuses
            // here. The decision precedes the repository read: existence is not probeable through a refusal.
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, TeamRolePermissions.RecordsRead, RouteRecord.Of(id), ct)
                .ConfigureAwait(false) is { } denied)
                return denied;
            var entity = await entities.GetByIdAsync(tenant, new RegistryEntityId(id), ct).ConfigureAwait(false);
            if (entity is null)
                return Results.NotFound();

            var now = new Instant(clock.GetUtcNow());
            var container = await edges.GetContainerAsAtAsync(tenant, entity.Id, now, ct).ConfigureAwait(false);
            var path = await edges.GetContainmentPathAsAtAsync(tenant, entity.Id, now, ct).ConfigureAwait(false);
            return Results.Ok(ToDetailWire(entity, container, path));
        });

        app.MapPost($"{RouteBase}/entities", async (CreateEntityBody body, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            if (body is null || string.IsNullOrWhiteSpace(body.Type) || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = "type_and_display_name_required" });

            var typeId = new EntityTypeId(body.Type.Trim());
            // The type must exist for this tenant (own row or shared seed) — never create an untyped entity.
            var tenantType = await types.GetTypeAsync(tenant, typeId, ct).ConfigureAwait(false);
            if (tenantType is null && types.GetSeed(typeId) is null)
                return Results.BadRequest(new { error = "unknown_type" });

            // Carry the type's effective pinned property-form binding onto the entity (the detail form).
            var propertyForm = await types.TryResolvePropertyFormAsync(tenant, typeId, ct).ConfigureAwait(false);
            var entity = new RegistryEntity
            {
                Id = RegistryEntityId.NewId(),
                TenantId = tenant,
                Type = typeId,
                DisplayName = body.DisplayName.Trim(),
                PropertyForm = propertyForm,
                ScanKey = string.IsNullOrWhiteSpace(body.ScanKey) ? null : body.ScanKey.Trim(),
                CreatedAt = new Instant(clock.GetUtcNow()),
            };
            await entities.UpsertAsync(entity, entity.CreatedAt, NodeCallerParty.Resolve(http).Value, ct).ConfigureAwait(false);

            return Results.Created($"{RouteBase}/entities/{entity.Id.Value}", ToDetailWire(entity, container: null, path: Array.Empty<RegistryEntityId>()));
        });
    }

    // ── GET /entities/{id}/tree[?asOf=] — direct contents + breadcrumb, as-of ────────
    private static void MapTree(
        IEndpointRouteBuilder app, IRegistryEntityRepository entities, ITypedRelationshipStore edges,
        IActiveTeamAccessor activeTeam, TimeProvider clock)
    {
        app.MapGet($"{RouteBase}/entities/{{id}}/tree", async (string id, string? asOf, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var container = new RegistryEntityId(id);
            var self = await entities.GetByIdAsync(tenant, container, ct).ConfigureAwait(false);
            if (self is null)
                return Results.NotFound();

            var at = ParseInstant(asOf, clock);
            var childIds = await edges.GetContentsAsAtAsync(tenant, container, at, ct).ConfigureAwait(false);
            var path = await edges.GetContainmentPathAsAtAsync(tenant, container, at, ct).ConfigureAwait(false);

            var children = new List<EntityWire>(childIds.Count);
            foreach (var childId in childIds)
            {
                var child = await entities.GetByIdAsync(tenant, childId, ct).ConfigureAwait(false);
                if (child is not null)
                    children.Add(ToEntityWire(child));
            }

            return Results.Ok(new TreeResponse(
                Container: container.Value,
                AsOf: at.Value.ToString("O", CultureInfo.InvariantCulture),
                Path: path.Select(p => p.Value).ToArray(),
                Children: children.ToArray()));
        });
    }

    // ── GET /entities/{id}/condition[?asOf=] — condition history ─────────────────────
    private static void MapCondition(
        IEndpointRouteBuilder app, IRegistryEntityRepository entities, IConditionAssessmentStore conditions,
        IActiveTeamAccessor activeTeam, TimeProvider clock)
    {
        app.MapGet($"{RouteBase}/entities/{{id}}/condition", async (string id, string? asOf, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var entityId = new RegistryEntityId(id);
            var self = await entities.GetByIdAsync(tenant, entityId, ct).ConfigureAwait(false);
            if (self is null)
                return Results.NotFound();

            var at = asOf is { Length: > 0 } ? ParseInstant(asOf, clock) : (Instant?)null;
            var history = await conditions.GetHistoryAsync(tenant, entityId, at, ct).ConfigureAwait(false);
            return Results.Ok(new ConditionHistoryResponse(entityId.Value, history.Select(ToConditionWire).ToArray()));
        });
    }

    // ── GET /entities/{id}/submissions — forms submitted into this record (#144) ─────
    private static void MapSubmissions(
        IEndpointRouteBuilder app, IRegistryEntityRepository entities, IFormSubmissionRecordStore submissions,
        IActiveTeamAccessor activeTeam)
    {
        app.MapGet($"{RouteBase}/entities/{{id}}/submissions", async (string id, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var entityId = new RegistryEntityId(id);
            // Same isolation posture as the condition route: an id absent under this tenant is an opaque 404
            // (never leaks another tenant's entity), and the store is itself tenant-scoped.
            var self = await entities.GetByIdAsync(tenant, entityId, ct).ConfigureAwait(false);
            if (self is null)
                return Results.NotFound();

            var rows = await submissions.ListForEntityAsync(tenant, entityId, ct).ConfigureAwait(false);
            return Results.Ok(new SubmissionListResponse(entityId.Value, rows.Select(ToSubmissionWire).ToArray()));
        });
    }

    // ── POST /edges — add a containment / located-at edge (effective now) ────────────
    private static void MapEdges(
        IEndpointRouteBuilder app, ITypedRelationshipStore edges, IActiveTeamAccessor activeTeam, TimeProvider clock)
    {
        app.MapPost($"{RouteBase}/edges", async (AddEdgeBody body, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            if (body is null || string.IsNullOrWhiteSpace(body.From) || string.IsNullOrWhiteSpace(body.To))
                return Results.BadRequest(new { error = "from_and_to_required" });
            if (!TryParseKind(body.Kind, out var kind))
                return Results.BadRequest(new { error = "invalid_kind", detail = "Expected 'contains', 'located-at' or 'part-of-system'." });

            var edge = new TypedRelationship
            {
                Id = TypedRelationshipId.NewId(),
                TenantId = tenant,
                Kind = kind,
                From = new RegistryEntityId(body.From.Trim()),
                To = new RegistryEntityId(body.To.Trim()),
                EffectiveFrom = new Instant(clock.GetUtcNow()),
            };

            try
            {
                var added = await edges.AddAsync(edge, NodeCallerParty.Resolve(http).Value, ct).ConfigureAwait(false);
                return Results.Created($"{RouteBase}/edges/{added.Id.Value}",
                    new EdgeWire(added.Id.Value, added.Kind.ToString(), added.From.Value, added.To.Value,
                        added.EffectiveFrom.Value.ToString("O", CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Static error only (no payload reflection): the store fail-closes on a self-edge, an
                // endpoint absent under this tenant (a cross-tenant / dangling id), an inverted window,
                // or a containment edge that would cycle / exceed the depth bound.
                return Results.BadRequest(new { error = "edge_rejected" });
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────
    private static Instant ParseInstant(string? asOf, TimeProvider clock)
        => asOf is { Length: > 0 } && DateTimeOffset.TryParse(
            asOf, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? new Instant(parsed)
            : new Instant(clock.GetUtcNow());

    private static bool TryParseKind(string? raw, out RelationshipKind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "contains": kind = RelationshipKind.Contains; return true;
            case "located-at": case "locatedat": kind = RelationshipKind.LocatedAt; return true;
            case "part-of-system": case "partofsystem": kind = RelationshipKind.PartOfSystem; return true;
            default: kind = default; return false;
        }
    }

    private static string[] TraitList(EntityTrait t)
    {
        var list = new List<string>(3);
        if (t.HasFlag(EntityTrait.Container)) list.Add("container");
        if (t.HasFlag(EntityTrait.Maintainable)) list.Add("maintainable");
        if (t.HasFlag(EntityTrait.Movable)) list.Add("movable");
        return list.ToArray();
    }

    private static EntityTrait ParseTraits(string[]? raw)
    {
        var traits = EntityTrait.None;
        if (raw is null) return traits;
        foreach (var t in raw)
        {
            switch (t?.Trim().ToLowerInvariant())
            {
                case "container": traits |= EntityTrait.Container; break;
                case "maintainable": traits |= EntityTrait.Maintainable; break;
                case "movable": traits |= EntityTrait.Movable; break;
            }
        }
        return traits;
    }

    /// <summary>Parses an optional pinned form reference; absent → null (an unbind). Returns false on a bad version.</summary>
    private static bool TryParseFormRef(FormRefBody? body, out FormBindingRef? formRef, out string error)
    {
        formRef = null;
        error = string.Empty;
        if (body is null || string.IsNullOrWhiteSpace(body.Definition))
            return true; // no binding → null

        if (string.IsNullOrWhiteSpace(body.Version))
        {
            error = "form_version_required";
            return false;
        }

        try
        {
            formRef = new FormBindingRef(
                new FormDefinitionId(body.Definition.Trim()),
                SemanticVersion.Parse(body.Version.Trim()));
            return true;
        }
        catch (FormatException)
        {
            error = "invalid_form_version";
            return false;
        }
    }

    /// <summary>Parses the optional narrowing disposition (the DefinitionPublisher vocabulary). Absent → null.</summary>
    private static bool TryParseDisposition(string? raw, out DefinitionDataDispositionKind? disposition, out string error)
    {
        disposition = null;
        error = string.Empty;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "": return true;
            case "migrate": disposition = DefinitionDataDispositionKind.Migrate; return true;
            case "flag": disposition = DefinitionDataDispositionKind.Flag; return true;
            case "accept": disposition = DefinitionDataDispositionKind.Accept; return true;
            default: error = "invalid_disposition"; return false;
        }
    }

    /// <summary>
    /// Classifies old→new for the loss-confirmation gate (ticket 155) with the same narrowing
    /// semantics as <see cref="DefinitionChangeClassifier"/>, implemented locally because the
    /// descriptor's fields are not the classifier's field-key/value-kind shape. A removal or
    /// narrowing — a dropped trait or discipline, a dropped or re-pointed parent type, a dropped or
    /// changed form binding (changes carry a distinct <c>…Changed:</c> label), an EFFECTIVELY
    /// lowered condition scale, a dropped planning default — is a loss; additions and value edits
    /// are not. Loss names derive from stored data only (never the request payload).
    /// </summary>
    private static IReadOnlyList<string> ClassifyLosses(EntityTypeDescriptor current, EntityTypeDescriptor next)
    {
        var losses = new List<string>();

        foreach (var trait in TraitList(current.Traits & ~next.Traits))
            losses.Add($"trait:{trait}");

        if (current.ParentType is { } parent && parent != next.ParentType)
            losses.Add($"parentType:{parent.Value}");

        // Binding CHANGES (same slot, different id or version) are gated deliberately: this route
        // cannot see the form-version's own widening/narrowing classification, so a repointed
        // version can narrow the form and must fail closed. Plumbing DefinitionPublisher's
        // classification in here could one-click pure widenings later.
        if (current.PropertyFormBinding is { } propertyForm && propertyForm != next.PropertyFormBinding)
            losses.Add(next.PropertyFormBinding is null
                ? $"propertyForm:{propertyForm.Definition.Value}"
                : $"propertyFormChanged:{propertyForm.Definition.Value}");

        foreach (var discipline in current.Disciplines)
            if (!next.Disciplines.Contains(discipline))
                losses.Add($"discipline:{discipline.Value}");

        foreach (var (discipline, binding) in current.InspectionFormBindings)
        {
            if (!next.InspectionFormBindings.TryGetValue(discipline, out var replacement))
                losses.Add($"inspectionForm:{discipline.Value}");
            else if (replacement != binding)
                losses.Add($"inspectionFormChanged:{discipline.Value}");
        }

        // Effective-scale comparison: a null ConditionScaleMax means the platform default
        // (ConditionRating.DefaultScaleMax, per the EntityTypeDescriptor doc), so null resolves to
        // the default on BOTH sides. A loss is only an effective lowering — null→2 is a real 5→2;
        // 4→null widens to 5 and 5→null is a no-op, both one-click.
        var effectiveCurrentScale = current.ConditionScaleMax ?? ConditionRating.DefaultScaleMax;
        var effectiveNextScale = next.ConditionScaleMax ?? ConditionRating.DefaultScaleMax;
        if (effectiveNextScale < effectiveCurrentScale)
            losses.Add("conditionScaleMax");

        if (current.ExpectedUsefulLifeYears is not null && next.ExpectedUsefulLifeYears is null)
            losses.Add("expectedUsefulLifeYears");

        if (current.TypicalReplacementCost is not null && next.TypicalReplacementCost is null)
            losses.Add("typicalReplacementCost");

        return losses;
    }

    /// <summary>Builds a validated descriptor from the request body; returns false + a static error code on failure.</summary>
    private static bool TryBuildDescriptor(TypeUpsertBody body, out EntityTypeDescriptor descriptor, out string error)
    {
        descriptor = null!;

        var traits = ParseTraits(body.Traits);
        if (traits == EntityTrait.None)
        {
            error = "at_least_one_trait_required";
            return false;
        }

        if (body.ConditionScaleMax is { } scale && scale < 2)
        {
            error = "condition_scale_min_two";
            return false;
        }

        if (body.ExpectedUsefulLifeYears is { } life && life < 0)
        {
            error = "life_years_non_negative";
            return false;
        }

        if (!TryParseFormRef(body.PropertyForm, out var propertyForm, out error))
            return false;

        Dictionary<DisciplineTag, FormBindingRef>? inspectionForms = null;
        if (body.InspectionForms is { Length: > 0 })
        {
            inspectionForms = new Dictionary<DisciplineTag, FormBindingRef>();
            foreach (var b in body.InspectionForms)
            {
                if (string.IsNullOrWhiteSpace(b.Discipline))
                {
                    error = "inspection_form_discipline_required";
                    return false;
                }

                if (!TryParseFormRef(new FormRefBody(b.Definition, b.Version), out var inspRef, out error))
                    return false;
                if (inspRef is null)
                {
                    error = "inspection_form_ref_required";
                    return false;
                }

                inspectionForms[new DisciplineTag(b.Discipline.Trim())] = inspRef;
            }
        }

        Money? cost = null;
        if (body.TypicalReplacementCost is { } m)
        {
            var currency = string.IsNullOrWhiteSpace(m.Currency) ? "USD" : m.Currency.Trim().ToUpperInvariant();
            cost = new Money(m.Amount, new CurrencyCode(currency));
        }

        var disciplines = body.Disciplines is { Length: > 0 }
            ? body.Disciplines.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => new DisciplineTag(d.Trim())).ToList()
            : null;

        var parentType = string.IsNullOrWhiteSpace(body.ParentType)
            ? (EntityTypeId?)null
            : new EntityTypeId(body.ParentType.Trim());

        error = string.Empty;
        descriptor = new EntityTypeDescriptor(
            DisplayName: body.DisplayName!.Trim(),
            Traits: traits,
            ParentType: parentType,
            PropertyFormBinding: propertyForm,
            Disciplines: disciplines,
            InspectionFormBindings: inspectionForms,
            ExpectedUsefulLifeYears: body.ExpectedUsefulLifeYears,
            TypicalReplacementCost: cost,
            ConditionScaleMax: body.ConditionScaleMax);
        return true;
    }

    private static TypeWire ToTypeWire(EntityTypeId id, EntityTypeDescriptor d, string provenance, bool overridesSeed) => new(
        Id: id.Value,
        DisplayName: d.DisplayName,
        Traits: TraitList(d.Traits),
        ParentType: d.ParentType?.Value,
        Disciplines: d.Disciplines.Select(x => x.Value).ToArray(),
        Provenance: provenance,
        OverridesSeed: overridesSeed,
        HasPropertyForm: d.PropertyFormBinding is not null,
        InspectionFormCount: d.InspectionFormBindings.Count,
        ConditionScaleMax: d.ConditionScaleMax,
        ExpectedUsefulLifeYears: d.ExpectedUsefulLifeYears,
        ReplacementCost: ToMoneyWire(d.TypicalReplacementCost));

    private static MoneyWire? ToMoneyWire(Money? money)
        => money is { } m ? new MoneyWire(m.Amount, m.Currency.Iso4217) : null;

    private static TypeDetailWire ToTypeDetailWire(
        EntityTypeId id, EntityTypeDescriptor d, string provenance, bool overridesSeed, bool seedExists, bool hasTenantRow,
        DispositionedWire? dispositioned = null) => new(
        Id: id.Value,
        DisplayName: d.DisplayName,
        Traits: TraitList(d.Traits),
        ParentType: d.ParentType?.Value,
        Disciplines: d.Disciplines.Select(x => x.Value).ToArray(),
        Provenance: provenance,
        OverridesSeed: overridesSeed,
        SeedExists: seedExists,
        HasTenantRow: hasTenantRow,
        PropertyForm: d.PropertyFormBinding is { } pf ? new PropertyFormWire(pf.Definition.Value, pf.PinnedVersion.ToString()) : null,
        InspectionForms: d.InspectionFormBindings
            .OrderBy(kv => kv.Key.Value, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new InspectionBindingWire(kv.Key.Value, kv.Value.Definition.Value, kv.Value.PinnedVersion.ToString()))
            .ToArray(),
        ConditionScaleMax: d.ConditionScaleMax,
        ExpectedUsefulLifeYears: d.ExpectedUsefulLifeYears,
        TypicalReplacementCost: ToMoneyWire(d.TypicalReplacementCost),
        Dispositioned: dispositioned);

    private static EntityWire ToEntityWire(RegistryEntity e) => new(
        Id: e.Id.Value,
        Type: e.Type.Value,
        DisplayName: e.DisplayName,
        ScanKey: e.ScanKey,
        CreatedAt: e.CreatedAt.Value.ToString("O", CultureInfo.InvariantCulture),
        RetiredAt: e.RetiredAt?.Value.ToString("O", CultureInfo.InvariantCulture));

    private static EntityDetailWire ToDetailWire(RegistryEntity e, RegistryEntityId? container, IReadOnlyList<RegistryEntityId> path) => new(
        Id: e.Id.Value,
        Type: e.Type.Value,
        DisplayName: e.DisplayName,
        ScanKey: e.ScanKey,
        CreatedAt: e.CreatedAt.Value.ToString("O", CultureInfo.InvariantCulture),
        RetiredAt: e.RetiredAt?.Value.ToString("O", CultureInfo.InvariantCulture),
        ContainerId: container?.Value,
        Path: path.Select(p => p.Value).ToArray(),
        PropertyForm: e.PropertyForm is { } pf ? new PropertyFormWire(pf.Definition.Value, pf.PinnedVersion.ToString()) : null);

    private static ConditionWire ToConditionWire(ConditionAssessment a) => new(
        Id: a.Id.Value,
        Entity: a.Entity.Value,
        Grade: a.Rating.Grade,
        ScaleMax: a.Rating.ScaleMax,
        Label: a.Rating.Label,
        Normalized: a.Rating.Normalized,
        ObservedAt: a.ObservedAt.Value.ToString("O", CultureInfo.InvariantCulture),
        AssessorRef: a.AssessorRef,
        SourceForm: a.SourceBinding?.FormDefinition.Value,
        SourceField: a.SourceBinding?.FieldPointer,
        Observations: a.Observations);

    private static SubmissionWire ToSubmissionWire(FormSubmissionRecord r) => new(
        InstanceId: r.InstanceId,
        FormId: r.FormId,
        SubmittedAt: r.SubmittedAt.Value.ToString("O", CultureInfo.InvariantCulture),
        AssessorRef: r.AssessorRef);

    // ── Request bodies ───────────────────────────────────────────────────────────────
    /// <summary>Body for POST /entities.</summary>
    public sealed record CreateEntityBody(string? Type, string? DisplayName, string? ScanKey);
    /// <summary>Body for POST /edges.</summary>
    public sealed record AddEdgeBody(string? Kind, string? From, string? To);

    /// <summary>Body for POST /types (create) and PUT /types/{id} (edit → override or update).</summary>
    /// <remarks>
    /// <c>Disposition</c> is the ticket-155 loss confirmation: <c>migrate</c> | <c>flag</c> |
    /// <c>accept</c> (the <see cref="DefinitionDataDispositionKind"/> vocabulary). Validated whenever
    /// present (POST and PUT alike); consulted only when a PUT removes or narrows the current
    /// effective descriptor, where it is required. Per ticket 155's parked note, <c>migrate</c> and
    /// <c>flag</c> are recorded in the response echo only — no migration or flagging is performed yet.
    /// </remarks>
    public sealed record TypeUpsertBody(
        string? Id,
        string? DisplayName,
        string[]? Traits,
        string? ParentType,
        string[]? Disciplines,
        FormRefBody? PropertyForm,
        InspectionBindingBody[]? InspectionForms,
        int? ConditionScaleMax,
        int? ExpectedUsefulLifeYears,
        MoneyBody? TypicalReplacementCost,
        string? Disposition = null);

    /// <summary>A pinned form reference in a request body (content-addressed id + pinned version).</summary>
    public sealed record FormRefBody(string? Definition, string? Version);
    /// <summary>A (discipline → inspection form) binding in a request body.</summary>
    public sealed record InspectionBindingBody(string? Discipline, string? Definition, string? Version);
    /// <summary>A currency-bound amount in a request body (capital-planning default).</summary>
    public sealed record MoneyBody(decimal Amount, string? Currency);

    // ── Response DTOs (camelCase wire) ─────────────────────────────────────────────────
    /// <summary>The effective type catalog.</summary>
    public sealed record TypeListResponse(TypeWire[] Types);
    /// <summary>An entity type (seed or tenant row), with its composable traits + discipline set.</summary>
    public sealed record TypeWire(
        string Id, string DisplayName, string[] Traits, string? ParentType, string[] Disciplines,
        string Provenance, bool OverridesSeed, bool HasPropertyForm,
        int InspectionFormCount, int? ConditionScaleMax, int? ExpectedUsefulLifeYears, MoneyWire? ReplacementCost);

    /// <summary>The full editable payload of one entity type (the Type Manager editor view).</summary>
    public sealed record TypeDetailWire(
        string Id, string DisplayName, string[] Traits, string? ParentType, string[] Disciplines,
        string Provenance, bool OverridesSeed, bool SeedExists, bool HasTenantRow,
        PropertyFormWire? PropertyForm, InspectionBindingWire[] InspectionForms,
        int? ConditionScaleMax, int? ExpectedUsefulLifeYears, MoneyWire? TypicalReplacementCost,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DispositionedWire? Dispositioned = null);

    /// <summary>
    /// Ticket-155 disposition echo on a PUT that narrowed: the confirmed kind + the losses it covered.
    /// Response-only — nothing is migrated or flagged yet (ticket 155's execution is parked).
    /// </summary>
    public sealed record DispositionedWire(string Kind, string[] Losses);

    /// <summary>A (discipline → inspection form) binding on a type (annex §3.2 type × discipline).</summary>
    public sealed record InspectionBindingWire(string Discipline, string Definition, string Version);

    /// <summary>A currency-bound amount over the wire (D-N capital planning).</summary>
    public sealed record MoneyWire(decimal Amount, string Currency);

    /// <summary>A list of entities.</summary>
    public sealed record EntityListResponse(EntityWire[] Entities);
    /// <summary>A registry entity summary.</summary>
    public sealed record EntityWire(
        string Id, string Type, string DisplayName, string? ScanKey, string CreatedAt, string? RetiredAt);

    /// <summary>A registry entity with its container + breadcrumb + pinned property form.</summary>
    public sealed record EntityDetailWire(
        string Id, string Type, string DisplayName, string? ScanKey, string CreatedAt, string? RetiredAt,
        string? ContainerId, string[] Path, PropertyFormWire? PropertyForm);
    /// <summary>The type's pinned property-form binding (the entity-detail form).</summary>
    public sealed record PropertyFormWire(string Definition, string Version);

    /// <summary>The containment tree view for a container at an instant.</summary>
    public sealed record TreeResponse(string Container, string AsOf, string[] Path, EntityWire[] Children);

    /// <summary>An entity's condition history.</summary>
    public sealed record ConditionHistoryResponse(string Entity, ConditionWire[] History);
    /// <summary>One condition assessment, with provenance back to the submission field.</summary>
    public sealed record ConditionWire(
        string Id, string Entity, int Grade, int ScaleMax, string? Label, double Normalized,
        string ObservedAt, string? AssessorRef, string? SourceForm, string? SourceField, string? Observations);

    /// <summary>The forms submitted into a record (#144 runtime form-fill).</summary>
    public sealed record SubmissionListResponse(string Entity, SubmissionWire[] Submissions);
    /// <summary>One form submitted into a record — the linkage that reopens the bound read-only view.</summary>
    public sealed record SubmissionWire(string InstanceId, string FormId, string SubmittedAt, string? AssessorRef);

    /// <summary>An added typed relationship edge.</summary>
    public sealed record EdgeWire(string Id, string Kind, string From, string To, string EffectiveFrom);
}
