using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Governance.Admission;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0055 — node-local dynamic-form DEFINITION authoring routes (the form
/// BUILDER persistence surface, 2026-06-27 production slice 1). Where
/// <see cref="FormsRoutes"/> RENDERS + SUBMITS a published form (the runtime
/// surface a tenant USER fills in), these routes SAVE + LOAD + LIST the
/// <see cref="FormDefinition"/> a tenant ADMIN authors in the Harborline App form
/// builder. The two surfaces share the engine's substrate
/// (<see cref="IFormDefinitionStore"/> + the kernel schema registry) but serve
/// different actors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes (all under <see cref="RouteBase"/>):</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/forms/definitions</c> — lists the tenant's
///     form definitions (id + version + title), newest authoring metadata for
///     the builder's "open a form" surface.</item>
///   <item><c>GET  /api/local-node/forms/definitions/{formId}</c> — loads the
///     current published authoring view of a definition (the overlay + per-field
///     authoring metadata the builder reconstructs its editing model from).</item>
///   <item><c>PUT  /api/local-node/forms/definitions/{formId}</c> — saves the
///     authored definition: synthesises the JSON Schema from the field metadata,
///     registers it in the kernel schema registry, then registers + publishes
///     the <see cref="FormDefinition"/>. Re-saving an existing id mints the next
///     patch version (definitions are immutable per <c>(id, version)</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant-scoping (INV-S1).</b> Exactly as <see cref="FormsRoutes"/>: the
/// Harborline App sends NO tenant id and NO principal in the BODY. The route resolves the
/// tenant from the active team (<see cref="NodeTenant.Resolve"/>) and the owner
/// server-side from the request's own selected-session principal (#3378), and the
/// <see cref="IFormDefinitionStore"/> enforces the tenant boundary on every
/// lookup/listing. A definition saved under tenant A is invisible to tenant B.
/// </para>
/// <para>
/// <b>The server owns schema synthesis (authoritative).</b> The Harborline App supplies
/// the authoring intent — per-field type / required / option values — and this
/// route synthesises the JSON Schema 2020-12 document from it, registers it in
/// the kernel schema registry (content-addressed, validated there), and binds the
/// resulting <see cref="SchemaId"/> as the definition's <c>SchemaRef</c>. The
/// overlay's field keys MUST exactly match the synthesised schema's properties
/// (the <see cref="HarborlineOverlay"/> invariant the store enforces) — both are
/// derived from the SAME field list, so they agree by construction.
/// </para>
/// <para>
/// <b>CP-safety (first-party only).</b> A definition authored through this route
/// is registered against the node's OWN schema registry by the local operator —
/// it is first-party by definition (the same posture as
/// <see cref="FormsRoutes"/>). The packet-carried / third-party CP-reachability
/// validator (ADR-0135 A1) is a separate, later gate; nothing here loads an
/// externally-supplied definition.
/// </para>
/// <para>
/// <b>Follow-ups (explicitly NOT in this slice):</b> live-instance SCHEMA
/// VERSIONING (an edited definition vs already-submitted instances), full
/// ERPNext-DocType parity (child-table grids, naming-series, the deferred
/// Customize-Form tail per ADR 0055 OQ-5/6), and the multi-role
/// <c>WriteRoles</c> authoring guidance (the N-1 least-privilege nit). Re-save
/// here always mints a new patch version and publishes it; reconciling
/// in-flight instances against a new version is the versioning follow-up.
/// </para>
/// </remarks>
public static class FormDefinitionRoutes
{
    /// <summary>Canonical route base for the node-local form-definition authoring surface.</summary>
    public const string RouteBase = "/api/local-node/forms/definitions";

    /// <summary>
    /// The section read/write roles a builder-authored definition grants. The
    /// UNION of the team display roles AND the node-operator fallback, so the
    /// single operator can render + author every field in BOTH the live host
    /// (role = "Admin") and the route-test harness (role = "node:operator") —
    /// the same posture <c>FormsDevSeeder</c> uses (without it a live GET returns
    /// every field unreadable). Narrowing to a least-privilege multi-role
    /// posture is the deferred <c>WriteRoles</c> authoring follow-up.
    /// </summary>
    private static readonly string[] OperatorSectionRoles =
        new[] { FormsRoutes.NodeOperatorRole, "Admin", "Member", "Viewer" };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Maps the definition-authoring routes onto <paramref name="app"/>, closing
    /// over the form-definition store, the kernel schema registry, and the
    /// active-team accessor from the OUTER host container (resolved here, not via
    /// <c>[FromServices]</c>, because the routes map onto the shared inner
    /// <c>WebApplication</c> whose provider is a separate container — bug-2849).
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        AuthorizedFormDefinitionLifecycle store,
        ISchemaRegistry schemaRegistry,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        IRestrictingDefinitionKindValidator? restrictingKinds = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(timeProvider);
        restrictingKinds ??= RestrictingDefinitionKindValidator.Shared;

        // GET /api/local-node/forms/definitions — list the tenant's definitions. The DEFAULT
        // stays published-only; ?includeDrafts=1|true additionally surfaces forms whose ONLY
        // revisions are drafts (ticket 156 review: a draft-only first save was otherwise
        // invisible to every list/GET read — the form looked lost). Draft rows carry a clear
        // status marker; published rows carry latestDraftVersion when a NEWER draft exists.
        app.MapGet(RouteBase, async (HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var includeDrafts = IsOptIn(http.Request.Query["includeDrafts"]);

            // Materialized once: rows need cross-revision knowledge (the latest draft per form).
            var revisions = new List<FormDefinition>();
            await foreach (var def in store.ListByTenantAsync(tenant, ct).ConfigureAwait(false))
            {
                revisions.Add(def);
            }

            var latestDraftByForm = new Dictionary<FormDefinitionId, SemanticVersion>();
            var publishedForms = new HashSet<FormDefinitionId>();
            foreach (var def in revisions)
            {
                if (def.Status == FormDefinitionStatus.Draft
                    && (!latestDraftByForm.TryGetValue(def.Id, out var seen) || def.Version.CompareTo(seen) > 0))
                {
                    latestDraftByForm[def.Id] = def.Version;
                }
                else if (def.Status == FormDefinitionStatus.Published)
                {
                    publishedForms.Add(def.Id);
                }
            }

            var summaries = new List<FormDefinitionSummaryDto>();
            foreach (var def in revisions)
            {
                if (def.Status == FormDefinitionStatus.Published)
                {
                    // A published authoring target; if a NEWER draft exists, say so — a client
                    // reloading this form must be able to detect the draft instead of silently
                    // minting its next draft off the stale published head.
                    summaries.Add(FormDefinitionSummaryDto.From(
                        def,
                        latestDraftByForm.TryGetValue(def.Id, out var draft) && draft.CompareTo(def.Version) > 0
                            ? draft.ToString()
                            : null));
                }
                else if (includeDrafts
                    && def.Status == FormDefinitionStatus.Draft
                    && !publishedForms.Contains(def.Id)
                    && def.Version.CompareTo(latestDraftByForm[def.Id]) == 0)
                {
                    // A draft-ONLY form: one row for its latest draft, clearly marked "Draft".
                    summaries.Add(FormDefinitionSummaryDto.From(def));
                }
            }

            return Results.Ok(summaries);
        });

        // GET /api/local-node/forms/definitions/{formId} — load the authoring view: the current
        // published head, or (ticket 156 review — draft discoverability) the latest DRAFT,
        // clearly marked, when no published head exists (a draft-only first save must stay
        // loadable without knowing its minted version).
        app.MapGet($"{RouteBase}/{{formId}}", async (string formId, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var def = await store
                .GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId), ct)
                .ConfigureAwait(false);
            var latestDraft = await LatestDraftAsync(store, tenant, new FormDefinitionId(formId), ct)
                .ConfigureAwait(false);
            if (def is null)
            {
                if (latestDraft is not null)
                {
                    return Results.Ok(FormDefinitionDto.From(latestDraft));
                }

                // INV-S1: not-found / cross-tenant are indistinguishable.
                return Results.NotFound(new { code = "form_definition.not_found", detail = new { formId } });
            }

            // A published head with a NEWER draft: surface it (latestDraftVersion) so a reloading
            // client can open the draft instead of editing the stale published head.
            return Results.Ok(FormDefinitionDto.From(
                def,
                latestDraft is not null && latestDraft.Version.CompareTo(def.Version) > 0
                    ? latestDraft.Version.ToString()
                    : null));
        });

        // PUT /api/local-node/forms/definitions/{formId} — save the authored definition.
        app.MapPut($"{RouteBase}/{{formId}}", async (
            string formId,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Ticket 151 stage-one gate: authoring (register + publish) a definition is a permissioned
            // act — forms:author — decided BEFORE any schema synthesis or persistence. A draft save
            // is still authoring, so the gate covers it too.
            var tenant = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.FormsAuthor, RouteRecord.Of(formId), ct) is { } denied)
                return denied;

            var id = new FormDefinitionId(formId);
            var now = timeProvider.GetUtcNow();
            var authority = new AuthorizationWriteContext(
                new ActorId(NodeCallerParty.Resolve(http).Value),
                tenant,
                now);
            var decision = await store.DecideAsync(id.Value, authority, ct).ConfigureAwait(false);

            SaveFormDefinitionRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<SaveFormDefinitionRequest>(
                    http.Request.Body, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "form_definition.malformed_request_body" });
            }

            if (request is null || request.Overlay is null || request.Overlay.Fields is null || request.Overlay.Sections is null)
            {
                return Results.BadRequest(new { code = "form_definition.overlay_required" });
            }

            // L1145 / ADR 0038: validate the raw discriminator BEFORE schema registration or any
            // definition-store write. BuildDefinition historically lowered an unknown action to
            // Visibility, silently replacing a restriction with a permitting presentation rule.
            foreach (var rule in request.Overlay.Rules ?? Array.Empty<RuleDto>())
            {
                var refusal = restrictingKinds.Validate(
                    RestrictingDefinitionKindFamily.RuleAction,
                    formId,
                    rule.Action,
                    nestedDefinitionId: rule.Id);
                if (refusal is not null)
                {
                    return Results.UnprocessableEntity(new
                    {
                        code = refusal.Code,
                        detail = new
                        {
                            target = rule.Id,
                            definition = refusal.DefinitionId,
                            unknownKind = refusal.UnknownKind,
                        },
                    });
                }
            }

            // Ticket 156 (L1539): a DRAFT save may be partial — a half-finished definition
            // (even zero sections) saves freely as Draft. A PUBLISH keeps the floor.
            var isDraft = request.Draft == true;
            if (!isDraft && request.Overlay.Sections.Count == 0)
            {
                return Results.BadRequest(new { code = "form_definition.overlay_section_required" });
            }

            var owner = ActingOwner(http);

            // The next version: bump the patch of the HIGHEST existing revision (published OR
            // draft), else 1.0.0. Minting off the current PUBLISHED head only (as this did before)
            // 409-bricked the form after a restore: a restored revision lands as a DRAFT at
            // publishedHead+1, so the very next PUT skipped that draft, re-minted the SAME version,
            // and RegisterAsync conflicted — permanently, since PUT is the only publish path and
            // there is no publish-draft route (deep review of #1686, Finding 1). Max-of-all matches
            // the restore route (they now share MintNextVersionAsync) + the preview store, so a
            // restore→save always advances PAST the restored draft.
            var version = await MintNextVersionAsync(store, tenant, id, ct).ConfigureAwait(false);

            // (1) Synthesise the JSON Schema from the field authoring metadata + register it
            //     (content-addressed in the kernel schema registry — the authoritative schema body).
            //     F-20: malformed validation-constraint config (unknown code, bad/missing param,
            //     min>max, uncompilable pattern, type-mismatched constraint) is rejected HERE,
            //     fail-closed, with a stable localizable code — before anything persists.
            string schemaJson;
            try
            {
                schemaJson = BuilderSchemaSynthesizer.Synthesize(request, id);
            }
            catch (FormDefinitionValidationException ex)
            {
                // Finding 4 (#1686 deep review): carry the offending node id as a structured
                // `target` so the client anchors the inline error off it instead of regex-scraping
                // the English message. Populated for the local synthesizer's constraint rejections
                // (ValidateConstraints knows the field); null for the governance validators whose
                // structured-target pass is a separate follow-up — the client keeps its regex
                // fallback for those.
                return Results.UnprocessableEntity(new { code = ex.Code ?? FormDefinitionCodes.ValidationRefused, detail = new { target = ex.Target } });
            }
            catch (ArgumentException ex)
            {
                // Ticket 094: the exception MESSAGE never reaches the wire (it is whatever the
                // throwing code happened to write). ParamName is a chosen, stable identifier.
                return Results.BadRequest(new
                {
                    code = "form_definition.authoring_metadata_invalid",
                    detail = new { parameter = ex.ParamName },
                });
            }

            SchemaId schemaRef;
            try
            {
                var schema = await schemaRegistry.RegisterAsync(schemaJson, ct: ct).ConfigureAwait(false);
                schemaRef = schema.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.BadRequest(new { code = "form_definition.schema_synthesis_rejected" });
            }

            // (2) Build the definition record from the overlay DTO + the synthesised schema ref.
            FormDefinition definition;
            try
            {
                definition = BuildDefinition(id, version, tenant, owner, schemaRef, request.Overlay, now);
            }
            catch (GateReferenceShapeException ex)
            {
                return Results.UnprocessableEntity(new { code = ex.Code, detail = new { field = ex.Field } });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    code = "form_definition.overlay_invalid",
                    detail = new { parameter = ex.ParamName },
                });
            }

            // (3) Register (+ publish, unless a draft save) — immutable per (id, version); a
            //     re-save mints the next patch. A draft (ticket 156) registers WITHOUT the
            //     publish gates: it is invisible to the form engine until a later PUT publishes
            //     it, at which point every gate below runs in full.
            try
            {
                if (isDraft)
                {
                    // A draft skips rule compilation (WIP rules are the point of a draft) and the
                    // authoring label gates, but NOT classification admission: drafts persist AND
                    // sync to peers, so residency/regime-relevant classification content must
                    // refuse fail-closed here too (ADR 0038).
                    FormDefinitionPublishAdmission.ValidateClassificationOrThrow(definition);
                }
                else
                {
                    // Authoring-time label gates (ticket 157): ROUTE-only — pack projection
                    // deliberately does not inherit them, so previously-valid signed pack
                    // content keeps (re)publishing on upgrade.
                    FormDefinitionPublishAdmission.ValidateAuthoringLabelsOrThrow(definition);

                    // Shared publish admission: F3 rule compilation rejects any Tier-2 expression or page
                    // guard the node cannot compile; SPINE-2 classification admission checks every field's
                    // resolved classification (form→section→container→field, monotonic-union) against
                    // known kinds, required effects, residency, regimes, and sensitive async-check inputs.
                    // Pack projection calls this SAME seam so signed content cannot bypass authoring safety.
                    FormDefinitionPublishAdmission.ValidateOrThrow(definition);
                }

                if (isDraft)
                    await store.RegisterAsync(definition, decision, ct).ConfigureAwait(false);
                else
                    await store.RegisterAndPublishAsync(definition, decision, ct).ConfigureAwait(false);
            }
            catch (FormDefinitionConflictException)
            {
                return Results.Conflict(new { code = "form_definition.revision_conflict", detail = new { formId, version = version.ToString() } });
            }
            catch (FormDefinitionValidationException ex)
            {
                // Surface the stable, locale-independent code (ADR 0055 Rev 7 item-tree
                // bounds) alongside the English message so the client localizes off the
                // CODE — the fleet's "validation errors are codes, not English literals"
                // rule. Null for the pre-Rev-7 message-only invariants.
                // Finding 4 (#1686 deep review): carry the offending node id as a structured
                // `target` so the client anchors the inline error off it instead of regex-scraping
                // the English message. Populated for the local synthesizer's constraint rejections
                // (ValidateConstraints knows the field); null for the governance validators whose
                // structured-target pass is a separate follow-up — the client keeps its regex
                // fallback for those.
                return Results.UnprocessableEntity(new { code = ex.Code ?? FormDefinitionCodes.ValidationRefused, detail = new { target = ex.Target } });
            }
            catch (RoleGateAdmissionException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    code = ex.Code,
                    detail = new
                    {
                        definition = ex.Finding.DefinitionId,
                        gate = ex.Finding.Gate,
                        role = ex.Finding.Subject,
                        rule = ex.Finding.Rule,
                    },
                });
            }
            catch (DefinitionProvenanceException ex)
            {
                return Results.UnprocessableEntity(new { code = ex.Code });
            }

            return Results.Ok(new SaveFormDefinitionResponse(formId, version.ToString()));
        });

        // GET /api/local-node/forms/definitions/{formId}/versions — the form's version HISTORY
        // (F-22, item 7). Every retained revision (ALL statuses), newest-first, enriched with
        // status + author + the restore-provenance link. Surfaces the immutable history the store
        // already keeps (each PUT mints a revision) — no new storage, no new store method.
        app.MapGet($"{RouteBase}/{{formId}}/versions", async (string formId, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var id = new FormDefinitionId(formId);
            var revisions = new List<FormVersionSummaryDto>();
            await foreach (var def in store.ListByTenantAsync(tenant, ct).ConfigureAwait(false))
            {
                if (def.Id != id)
                {
                    continue;
                }
                revisions.Add(FormVersionSummaryDto.From(def));
            }
            // The store lists (id asc, version asc); the history reads newest-first.
            revisions.Reverse();
            return Results.Ok(revisions);
        });

        // GET /api/local-node/forms/definitions/{formId}/versions/{version} — view ONE revision
        // read-only (F-22). Serves ANY status incl. an unpublished DRAFT, so a restored draft can
        // be loaded into the builder (GET-by-id only returns the current published head).
        app.MapGet($"{RouteBase}/{{formId}}/versions/{{version}}", async (string formId, string version, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            SemanticVersion parsed;
            try
            {
                parsed = SemanticVersion.Parse(version);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { code = "form_definition.version_malformed", detail = new { version } });
            }
            try
            {
                var def = await store.GetAsync(new DefinitionCoordinates(
                    tenant, formId, parsed.ToString()), ct).ConfigureAwait(false);
                return Results.Ok(FormDefinitionDto.From(def));
            }
            catch (FormDefinitionNotFoundException)
            {
                // INV-S1: not-found / cross-tenant are indistinguishable.
                return Results.NotFound(new { code = "form_definition.revision_not_found", detail = new { formId, version } });
            }
        });

        // POST /api/local-node/forms/definitions/{formId}/restore — restore a prior revision as a
        // NEW DRAFT derived from it (F-22, item 7). Append-only: history is NEVER mutated — the
        // source stays exactly as-is; the restored copy is a fresh revision recording its
        // `Lineage` (derived-from). Lands as Draft (Register only, no Publish) so the builder can
        // reopen + re-save it (which then publishes the next version). Draft still syncs to peers;
        // the history contract marks it unsafe for isolated staging.
        app.MapPost($"{RouteBase}/{{formId}}/restore", async (
            string formId,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Ticket 151: restore REGISTERS a new draft revision — the same authoring permission gates it.
            var tenant = NodeTenant.Resolve(activeTeam);
            if (await RequestAuthorization.RefusalAsync(
                    http, tenant, Permission.FormsAuthor, RouteRecord.Of(formId), ct) is { } denied)
                return denied;

            var id = new FormDefinitionId(formId);
            var now = timeProvider.GetUtcNow();
            var authority = new AuthorizationWriteContext(
                new ActorId(NodeCallerParty.Resolve(http).Value),
                tenant,
                now);
            var decision = await store.DecideAsync(id.Value, authority, ct).ConfigureAwait(false);

            RestoreVersionRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<RestoreVersionRequest>(
                    http.Request.Body, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "form_definition.malformed_request_body" });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Version))
            {
                return Results.BadRequest(new { code = "form_definition.restore_version_required" });
            }

            var owner = ActingOwner(http);
            SemanticVersion sourceVersion;
            try
            {
                sourceVersion = SemanticVersion.Parse(request.Version);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { code = "form_definition.version_malformed", detail = new { version = request.Version } });
            }

            FormDefinition source;
            try
            {
                source = await store.GetAsync(new DefinitionCoordinates(
                    tenant, id.Value, sourceVersion.ToString()), ct).ConfigureAwait(false);
            }
            catch (FormDefinitionNotFoundException)
            {
                return Results.NotFound(new { code = "form_definition.revision_not_found", detail = new { formId, version = request.Version } });
            }

            // Mint the next patch off the HIGHEST existing revision (published OR draft), so a
            // restored draft never collides with a later revision. Shares MintNextVersionAsync with
            // the PUT save so the two mint IDENTICALLY — closing the divergence class the #1686 deep
            // review flagged (restore minted max-of-all while PUT minted published-head).
            var newVersion = await MintNextVersionAsync(store, tenant, id, ct).ConfigureAwait(false);

            var restored = source with
            {
                Version = newVersion,
                Status = FormDefinitionStatus.Draft,
                Owner = owner,
                Lineage = new FormDefinitionLineage(id, sourceVersion),
                CreatedAt = now,
                UpdatedAt = now,
            };

            try
            {
                // RegisterAsync only — a restored revision lands as a DRAFT (never auto-published).
                // The lineage parent (the source revision) exists, so the store's parent-must-exist
                // validation passes.
                await store.RegisterAsync(restored, decision, ct).ConfigureAwait(false);
            }
            catch (FormDefinitionConflictException)
            {
                return Results.Conflict(new { code = "form_definition.revision_conflict", detail = new { formId, version = newVersion.ToString() } });
            }

            return Results.Ok(new SaveFormDefinitionResponse(formId, newVersion.ToString()));
        });
    }

    /// <summary>
    /// Mints the next patch version for <paramref name="id"/> off the MAX of ALL its retained
    /// revisions (published OR draft), or <c>1.0.0</c> when it has none. Shared by the PUT save
    /// and the restore route so both advance PAST a restored draft identically. This is the
    /// #1686 restore-then-save 409 fix: minting off the published head skipped drafts, so a PUT
    /// after a restore re-minted the restored draft's version and conflicted forever.
    /// </summary>
    private static async ValueTask<SemanticVersion> MintNextVersionAsync(
        AuthorizedFormDefinitionLifecycle store, TenantId tenant, FormDefinitionId id, CancellationToken ct)
    {
        SemanticVersion? highest = null;
        await foreach (var def in store.ListByTenantAsync(tenant, ct).ConfigureAwait(false))
        {
            if (def.Id == id && (highest is null || def.Version.CompareTo(highest.Value) > 0))
            {
                highest = def.Version;
            }
        }
        return highest is { } h
            ? new SemanticVersion(h.Major, h.Minor, h.Patch + 1)
            : new SemanticVersion(1, 0, 0);
    }

    /// <summary>An opt-in query flag: exactly <c>1</c> or <c>true</c> (case-insensitive) —
    /// the SAME accepted values as the app lanes' fixture opt-ins, documented on both sides.</summary>
    private static bool IsOptIn(Microsoft.Extensions.Primitives.StringValues value)
        => string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The HIGHEST-versioned Draft revision of <paramref name="id"/>, or null when it
    /// has none. Same O(revisions) tenant scan as <see cref="MintNextVersionAsync"/> — the store
    /// exposes no per-form index and these lists are authoring-sized.</summary>
    private static async ValueTask<FormDefinition?> LatestDraftAsync(
        AuthorizedFormDefinitionLifecycle store, TenantId tenant, FormDefinitionId id, CancellationToken ct)
    {
        FormDefinition? latest = null;
        await foreach (var def in store.ListByTenantAsync(tenant, ct).ConfigureAwait(false))
        {
            if (def.Id == id && def.Status == FormDefinitionStatus.Draft
                && (latest is null || def.Version.CompareTo(latest.Version) > 0))
            {
                latest = def;
            }
        }

        return latest;
    }

    /// <summary>
    /// The acting member stamped as the definition owner, read from the same
    /// <c>HttpContext.Features</c> seam nine shipped route files already use
    /// (<see cref="NodeCallerParty.Resolve(HttpContext)"/>): the live selected-session
    /// principal's canonical Party on the web plane, the operator party on the desktop
    /// plane, and a refusal when a web-plane request carries no principal at all.
    /// </summary>
    /// <remarks>
    /// Resolved PER REQUEST (#3378). The owner used to be a constant captured once in
    /// <c>HostedFormsApiEndpoint.StartAsync</c>, so every member's authored definition
    /// was owned by the same identity. That is an attribution failure, not an
    /// authorization one, so no PEP is involved and none is required.
    /// </remarks>
    private static IdentityRef ActingOwner(HttpContext http) =>
        new("user", NodeCallerParty.Resolve(http).Value);

    /// <summary>
    /// Builds a <see cref="FormDefinition"/> from the wire overlay DTO. Maps the
    /// authoring overlay onto the canonical foundation-forms model + binds the
    /// synthesised <paramref name="schemaRef"/>.
    /// </summary>
    internal static FormDefinition BuildDefinition(
        FormDefinitionId id,
        SemanticVersion version,
        TenantId tenant,
        IdentityRef owner,
        SchemaId schemaRef,
        OverlayDto overlay,
        DateTimeOffset now)
    {
        var fields = overlay.Fields.ToDictionary(
            kv => kv.Key,
            kv => new FieldOverlay(
                Label: kv.Value.Label.ToModel(kv.Key),
                HelpText: kv.Value.HelpText?.ToModel(),
                ControlHint: kv.Value.ControlHint,
                PiiSensitivity: string.Equals(kv.Value.PiiSensitivity, "Sensitive", StringComparison.OrdinalIgnoreCase)
                    ? PiiSensitivity.Sensitive
                    : PiiSensitivity.None,
                FieldReadRoles: kv.Value.ReadRoles,
                FieldWriteRoles: kv.Value.WriteRoles,
                // F-17: persist the per-control config through to the stored overlay so
                // currency code / file accept + multiple round-trip on the next GET.
                Config: kv.Value.Config?.ToModel(),
                // SPINE-2 (item 6): persist the field-grain classification aspect (the tags the
                // resolver + Store/Read PEPs key off; round-trips on the next GET).
                Aspects: kv.Value.Aspects?.ToModel(),
                FieldReadStandings: AccessGateDto.ToStandings(kv.Value.ReadStandings, "fields.readStandings"),
                FieldWriteStandings: AccessGateDto.ToStandings(kv.Value.WriteStandings, "fields.writeStandings")));

        var sections = overlay.Sections.Select(s => new FormSection(
            Id: s.Id,
            Title: s.Title.ToModel(s.Id),
            Fields: s.Fields,
            // Author-side access posture: the operator-role union (see OperatorSectionRoles).
            // The Harborline App never authors raw roles in this slice — the multi-role least-privilege
            // WriteRoles guidance is the deferred follow-up.
            Access: s.Access?.ToSectionAccess()
                ?? new SectionAccess(ReadRoles: OperatorSectionRoles, WriteRoles: OperatorSectionRoles),
            Layout: s.Layout?.ToModel(),
            FieldPlacement: s.FieldPlacement is null || s.FieldPlacement.Count == 0
                ? null
                : s.FieldPlacement.ToDictionary(kv => kv.Key, kv => kv.Value.ToModel()),
            // SPINE-2 (item 6): the section-grain classification aspect (a resolver grain between
            // the form and its fields; a section tag flows to every field in the section).
            Aspects: s.Aspects?.ToModel(),
            // ADR 0055 Rev 7: reconstruct the recursive item tree from the wire so the
            // nested sub-form structure is PERSISTED (and reaches the fail-closed
            // FormTreeLimits bounds ValidateOverlayOrThrow runs at RegisterAsync). Absent
            // ⇒ null (a flat, depth-1 section — byte-identical to a pre-Rev-7 definition).
            Items: s.Items is { Count: > 0 }
                ? s.Items.Select(i => i.ToModel()).ToList()
                : null)).ToList();

        var rules = (overlay.Rules ?? Array.Empty<RuleDto>())
            .Select(r => new RuleDefinition(
                Envelope: new DefinitionEnvelope<string, string, TenantId, string?>(
                    Identity: r.Id,
                    Version: version.ToString(),
                    Tenant: tenant,
                    CascadeLayer: CascadeLayer.Tenant,
                    Provenance: null,
                    Requires: Array.Empty<DefinitionRequirement>()),
                Tier: Enum.TryParse<RuleTier>(r.Tier, ignoreCase: true, out var tier) ? tier : RuleTier.JsonSchema,
                Scope: Enum.TryParse<RuleScope>(r.Scope, ignoreCase: true, out var scope) ? scope : RuleScope.Field,
                ScopeTarget: r.ScopeTarget,
                Expression: r.Expression,
                Action: Enum.TryParse<RuleActionKind>(r.Action, ignoreCase: true, out var action) ? action : RuleActionKind.Visibility))
            .ToList();

        // F-14: reconstruct the wizard-page grain from the wire so pages PERSIST and
        // reach the fail-closed page invariants ValidateOverlayOrThrow runs at
        // RegisterAsync. Absent ⇒ null (a pageless definition — byte-identical pre-F-14).
        var pages = overlay.Pages is { Count: > 0 }
            ? overlay.Pages.Select(p => p.ToModel()).ToList()
            : null;

        // F-20: reconstruct the async validation checks so they PERSIST and reach the
        // fail-closed check invariants ValidateOverlayOrThrow runs at RegisterAsync.
        var asyncChecks = overlay.AsyncChecks is { Count: > 0 }
            ? overlay.AsyncChecks.Select(c => c.ToModel()).ToList()
            : null;

        return new FormDefinition(
            Id: id,
            Version: version,
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: owner,
            SchemaRef: schemaRef,
            Overlay: new HarborlineOverlay(
                Fields: fields,
                Sections: sections,
                Rules: rules,
                Title: overlay.Title?.ToModel(),
                Description: overlay.Description?.ToModel(),
                Pages: pages,
                Wizard: overlay.Wizard?.ToModel(),
                AsyncChecks: asyncChecks,
                Aspects: overlay.Aspects?.ToModel()),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);
    }
}

/// <summary>
/// The single fail-closed publish-admission seam for locally authored and pack-projected form definitions.
/// Keeping both paths here prevents a signed pack from bypassing rule compilation or classification policy.
/// </summary>
internal static class FormDefinitionPublishAdmission
{
    private static readonly IPolicyAdmissionValidator ClassificationAdmission = BuildClassificationAdmission();

    /// <summary>
    /// The builder client's placeholder-label FAMILY (ticket 157 / L1543, patterns.md §20:
    /// nothing is ever created called "New field" that you must then find and rename) — the
    /// bare placeholder plus its dedup variants ("New field 2", "New field (copy)"). Anchored,
    /// never a contains-match: a legitimate label that merely CONTAINS the phrase publishes.
    /// A Draft may hold a placeholder (ticket 156); a route publish refuses it, naming the field.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PlaceholderLabelPattern = new(
        @"^new field( \d+| \(copy\))?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The safety gates EVERY publish path runs — locally authored (route PUT) and
    /// pack-projected alike: F3 rule compilation + SPINE-2 classification admission. The
    /// AUTHORING-time label gates live in <see cref="ValidateAuthoringLabelsOrThrow"/> and are
    /// deliberately NOT here: a new authoring rule must never retroactively invalidate
    /// previously-valid signed pack content (which would strand its pinned tuple un-republishable
    /// on upgrade/re-projection).</summary>
    public static void ValidateOrThrow(FormDefinition definition)
    {
        Harborline.Api.Foundation.Forms.Engine.RuleCompileAdmission.ValidateOrThrow(definition);
        ClassificationAdmission.ValidateAtPublish(definition);
    }

    /// <summary>The SPINE-2 classification/residency admission ALONE — the gate a DRAFT save
    /// still runs (drafts persist and sync to peers, so a restricted classification refuses
    /// fail-closed at the same seam a publish does; ADR 0038). Rule compilation stays
    /// draft-exempt by design: WIP rules are the point of a draft.</summary>
    public static void ValidateClassificationOrThrow(FormDefinition definition)
        => ClassificationAdmission.ValidateAtPublish(definition);

    /// <summary>Refuses ROUTE-publishing any field with no usable label at all, or one still
    /// labelled with the <see cref="PlaceholderLabelPattern"/> family (any locale value). The
    /// 422 carries a stable code and the offending field key as the structured <c>target</c>.
    /// Route-only: pack projection never runs this (see <see cref="ValidateOrThrow"/>).</summary>
    public static void ValidateAuthoringLabelsOrThrow(FormDefinition definition)
    {
        foreach (var (key, field) in definition.Overlay.Fields)
        {
            var values = field.Label?.Values;

            // A field with NO usable label (no values, or every value blank) is strictly
            // worse than a placeholder — refuse it with its own stable code.
            if (values is not { Count: > 0 } || values.Values.All(string.IsNullOrWhiteSpace))
            {
                throw new FormDefinitionValidationException(
                    definition.Id,
                    $"field '{key}' has no label; name the field before publishing.",
                    FormDefinitionCodes.LabelMissing,
                    key);
            }

            foreach (var value in values.Values)
            {
                if (value is not null && PlaceholderLabelPattern.IsMatch(value.Trim()))
                {
                    throw new FormDefinitionValidationException(
                        definition.Id,
                        $"field '{key}' still carries the placeholder label \"{value.Trim()}\"; name the field before publishing.",
                        FormDefinitionCodes.LabelPlaceholder,
                        key);
                }
            }
        }
    }

    private static IPolicyAdmissionValidator BuildClassificationAdmission()
    {
        var registry = new InMemoryPolicyRegistry();
        return new PolicyAdmissionValidator(new AspectResolver(registry), registry);
    }
}

// ── Schema synthesis (authoring metadata → JSON Schema 2020-12) ────────────────

/// <summary>
/// Synthesises a JSON Schema 2020-12 document from the builder's per-field
/// authoring metadata. The overlay's field keys MUST exactly match the schema's
/// <c>properties</c> (the <see cref="HarborlineOverlay"/> invariant) — both are
/// derived from the same field list here, so they agree by construction.
/// </summary>
/// <remarks>
/// F-20: the full authored validation set (<c>minLength</c> / <c>maxLength</c> /
/// <c>pattern</c> / <c>minimum</c> / <c>maximum</c>) now lowers onto the property
/// schema — the Tier-1 constraints live on the JSON Schema (their canonical home),
/// so the registry's submit-time validation enforces them with the SAME stable
/// codes + params the client mirrors. Malformed constraint config is rejected
/// fail-closed with a stable <see cref="FormDefinitionCodes"/> code.
/// </remarks>
internal static class BuilderSchemaSynthesizer
{
    /// <summary>The validation-constraint codes the builder may author (F-20).</summary>
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        "required", "minLength", "maxLength", "pattern", "minimum", "maximum",
    };

    /// <summary>Field types whose synthesized JSON type is <c>number</c>.</summary>
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "number", "percentage",
    };

    /// <summary>
    /// Field types that are MONEY — carried on the wire as a decimal STRING, never an IEEE-754
    /// number (finding F1 / deep review of #1683; fleet money doctrine "Money typing, never bare
    /// decimal"). The <c>currency</c> renderer already emits a raw decimal string (SchemaForm's
    /// CurrencyField — no <c>Number()</c> round-trip), so the schema MUST type it as a string to
    /// match; typing it as <c>number</c> both rejected the renderer's string value AND forced the
    /// rule engine's Σ-aggregate onto the lossy double fold (a balanced entry with fractional cents
    /// then false-rejected). A money value validates against <see cref="MoneyPattern"/>.
    /// </summary>
    private static readonly HashSet<string> MoneyTypes = new(StringComparer.Ordinal)
    {
        "currency",
    };

    /// <summary>JSON-Schema <c>pattern</c> for a money value: an optional-sign decimal, OR empty
    /// (an untouched/cleared optional money field rides the wire as <c>""</c>, exactly like any
    /// optional string; a REQUIRED money field additionally gets <c>minLength: 1</c> so <c>""</c>
    /// fails closed). The exact-decimal engine (<c>MoneyDecimal.Parse</c>) owns arithmetic; this
    /// pattern only fences the wire shape at the schema boundary.</summary>
    private const string MoneyPattern = @"^$|^-?[0-9]+(\.[0-9]+)?$";

    /// <summary>Field types whose synthesized JSON type is <c>boolean</c>.</summary>
    private static readonly HashSet<string> BooleanTypes = new(StringComparer.Ordinal)
    {
        "checkbox", "boolean-toggle",
    };

    public static string Synthesize(SaveFormDefinitionRequest request, FormDefinitionId id)
    {
        var fieldsMeta = request.FieldsMeta ?? new Dictionary<string, FieldMetaDto>();
        var orderedFieldNames = request.Overlay.Fields.Keys.ToList();

        // ADR 0055 Rev 7 / F-24 (item 5): a section MAY carry a nested item tree (groups +
        // collections). A field nested inside a container is NOT a top-level candidate property —
        // it lives inside its container's object (group) / row object (collection). Collect the
        // nested field keys so the top-level loop SKIPS them and the top-level containers emit
        // their own nested schemas. A purely-flat definition (no section carries Items) has an
        // empty nestedKeys set, so the top-level loop below is byte-identical to the pre-tree
        // synthesizer (item-6 back-compat).
        var nestedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in request.Overlay.Sections)
        {
            CollectNestedFieldKeys(section.Items, insideContainer: false, nestedKeys);
        }

        var required = new List<string>();

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
            w.WriteString("type", "object");

            w.WritePropertyName("properties");
            w.WriteStartObject();

            // (a) Top-level flat fields — in overlay field-registry order (byte-stable). Skip a
            //     field that lives inside a container (it is emitted inside that container below).
            foreach (var name in orderedFieldNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("A field name must be non-empty.");
                }
                if (nestedKeys.Contains(name))
                {
                    continue;
                }

                fieldsMeta.TryGetValue(name, out var meta);
                var constraints = ValidateConstraints(id, name, meta);
                var isRequired = meta?.Required == true || constraints.ContainsKey("required");
                w.WritePropertyName(name);
                WritePropertySchema(w, meta, constraints, isRequired);

                if (isRequired)
                {
                    required.Add(name);
                }
            }

            // (b) Top-level containers: a `group` → nested object property; a `collection` → an
            //     array of row objects (F-24 — the child-table schema). Recursive for deep trees.
            foreach (var section in request.Overlay.Sections)
            {
                foreach (var node in section.Items ?? Array.Empty<FormItemDto>())
                {
                    if (IsContainerNode(node))
                    {
                        w.WritePropertyName(node.Key);
                        WriteContainerSchema(w, node, fieldsMeta, id);
                        if (ContainerIsRequired(node))
                        {
                            required.Add(node.Key);
                        }
                    }
                    else if (IsReferenceNode(node))
                    {
                        // A D4 reference nests the resolved unit's values under its key; the unit
                        // validates its own shape at ITS registration, so emit a permissive object
                        // here so `additionalProperties:false` does not reject the reference data.
                        w.WritePropertyName(node.Key);
                        w.WriteStartObject();
                        w.WriteString("type", "object");
                        w.WriteEndObject();
                    }
                }
            }
            w.WriteEndObject(); // properties

            if (required.Count > 0)
            {
                w.WritePropertyName("required");
                w.WriteStartArray();
                foreach (var r in required)
                {
                    w.WriteStringValue(r);
                }
                w.WriteEndArray();
            }

            // Closed schema: a builder-authored form does not accept undeclared fields.
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsContainerNode(FormItemDto node) =>
        string.Equals(node.Kind, "group", StringComparison.Ordinal)
        || string.Equals(node.Kind, "collection", StringComparison.Ordinal);

    private static bool IsReferenceNode(FormItemDto node) =>
        string.Equals(node.Kind, "reference", StringComparison.Ordinal);

    /// <summary>Marks every <c>field</c> key that lives INSIDE any container (at any depth) so the
    /// top-level property loop skips it. Top-level field items keep their top-level property.</summary>
    private static void CollectNestedFieldKeys(
        IReadOnlyList<FormItemDto>? items, bool insideContainer, HashSet<string> nested)
    {
        if (items is null)
        {
            return;
        }
        foreach (var node in items)
        {
            if (string.Equals(node.Kind, "field", StringComparison.Ordinal))
            {
                if (insideContainer)
                {
                    nested.Add(node.Key);
                }
            }
            else if (IsContainerNode(node))
            {
                CollectNestedFieldKeys(node.Items, insideContainer: true, nested);
            }
            // content / action / reference carry no field keys to collect.
        }
    }

    /// <summary>A group is cardinality-1; a collection is required only when it must carry ≥1 row
    /// (cardinality.min ≥ 1). A group is NOT required at its parent — an untouched all-optional
    /// group must not be forced to emit an empty object; its own required children are enforced
    /// when it is present.</summary>
    private static bool ContainerIsRequired(FormItemDto node) =>
        string.Equals(node.Kind, "collection", StringComparison.Ordinal)
        && node.Cardinality is { Min: > 0 };

    /// <summary>Writes a container's JSON-Schema fragment: a group → an object schema; a collection
    /// → an array of row objects (<c>minItems</c>/<c>maxItems</c> from cardinality).</summary>
    private static void WriteContainerSchema(
        Utf8JsonWriter w, FormItemDto node, IReadOnlyDictionary<string, FieldMetaDto> fieldsMeta, FormDefinitionId id)
    {
        if (string.Equals(node.Kind, "collection", StringComparison.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("type", "array");
            if (node.Cardinality is { Min: > 0 } card)
            {
                w.WriteNumber("minItems", card.Min);
            }
            if (node.Cardinality?.Max is { } max)
            {
                w.WriteNumber("maxItems", max);
            }
            w.WritePropertyName("items");
            WriteObjectSchema(w, node.Items, fieldsMeta, id);
            w.WriteEndObject();
        }
        else // group → object
        {
            WriteObjectSchema(w, node.Items, fieldsMeta, id);
        }
    }

    /// <summary>Writes a closed object schema for a container's children — nested fields (with
    /// their Tier-1 constraints) + nested sub-containers, recursively.</summary>
    private static void WriteObjectSchema(
        Utf8JsonWriter w, IReadOnlyList<FormItemDto>? items, IReadOnlyDictionary<string, FieldMetaDto> fieldsMeta, FormDefinitionId id)
    {
        var required = new List<string>();
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WritePropertyName("properties");
        w.WriteStartObject();
        foreach (var child in items ?? Array.Empty<FormItemDto>())
        {
            if (string.Equals(child.Kind, "field", StringComparison.Ordinal))
            {
                fieldsMeta.TryGetValue(child.Key, out var meta);
                var constraints = ValidateConstraints(id, child.Key, meta);
                w.WritePropertyName(child.Key);
                WritePropertySchema(w, meta, constraints);
                if (meta?.Required == true || constraints.ContainsKey("required"))
                {
                    required.Add(child.Key);
                }
            }
            else if (IsContainerNode(child))
            {
                w.WritePropertyName(child.Key);
                WriteContainerSchema(w, child, fieldsMeta, id);
                if (ContainerIsRequired(child))
                {
                    required.Add(child.Key);
                }
            }
            else if (IsReferenceNode(child))
            {
                w.WritePropertyName(child.Key);
                w.WriteStartObject();
                w.WriteString("type", "object");
                w.WriteEndObject();
            }
            // content / action bind no value — never a schema property.
        }
        w.WriteEndObject(); // properties
        if (required.Count > 0)
        {
            w.WritePropertyName("required");
            w.WriteStartArray();
            foreach (var r in required)
            {
                w.WriteStringValue(r);
            }
            w.WriteEndArray();
        }
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();
    }

    /// <summary>
    /// F-20 fail-closed constraint-config admission: unknown codes, missing / unparseable
    /// params, negative lengths, conflicting bounds (<c>min &gt; max</c>), uncompilable
    /// regex patterns, and constraints inapplicable to the field's type are all rejected
    /// with a stable localizable code. Returns the parsed constraint map (code → param).
    /// </summary>
    private static Dictionary<string, string?> ValidateConstraints(FormDefinitionId id, string field, FieldMetaDto? meta)
    {
        var parsed = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (meta?.Validations is not { Count: > 0 } validations)
        {
            return parsed;
        }

        var type = meta.Type ?? "text";
        var isNumeric = NumericTypes.Contains(type);
        var isBoolean = BooleanTypes.Contains(type);
        // F1 (deep review of #1683): money is a decimal STRING on the wire, not a number and not a
        // free string. The Tier-1 length/pattern keywords don't apply (the engine-owned money
        // pattern is the only shape fence) and numeric bounds (minimum/maximum) are NOT numeric
        // JSON-Schema keywords on a string — supporting money-range bounds is a follow-up (a
        // money-comparison rule, never a silently-ignored `minimum` on a string). So a money field
        // carries `required` ONLY; every other constraint is rejected fail-closed rather than
        // silently no-op'ing.
        var isMoney = MoneyTypes.Contains(type);

        // Finding 4 (#1686 deep review): every throw here passes `field` as the structured
        // `target` (the 4th ctor arg) so the 422 body carries the offending node id and the
        // authoring client anchors the inline error off it instead of regex-scraping the message.
        foreach (var v in validations)
        {
            var code = v.Code ?? string.Empty;
            if (!KnownCodes.Contains(code))
            {
                throw new FormDefinitionValidationException(
                    id, $"field '{field}' has a validation constraint with unknown code '{code}'.",
                    FormDefinitionCodes.ConstraintUnknownCode, field);
            }

            if (!parsed.TryAdd(code, v.Param))
            {
                throw new FormDefinitionValidationException(
                    id, $"field '{field}' declares constraint '{code}' more than once.",
                    FormDefinitionCodes.ConstraintBadParam, field);
            }

            switch (code)
            {
                case "required":
                    break; // no param, applies to every type.

                case "minLength" or "maxLength":
                    if (isNumeric || isBoolean || isMoney)
                    {
                        throw new FormDefinitionValidationException(
                            id, $"field '{field}' ({type}) cannot carry a '{code}' constraint.",
                            FormDefinitionCodes.ConstraintTypeMismatch, field);
                    }
                    if (!int.TryParse(v.Param, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var len) || len < 0)
                    {
                        throw new FormDefinitionValidationException(
                            id, $"field '{field}' constraint '{code}' needs a non-negative integer parameter (got '{v.Param}').",
                            FormDefinitionCodes.ConstraintBadParam, field);
                    }
                    break;

                case "minimum" or "maximum":
                    if (!isNumeric)
                    {
                        throw new FormDefinitionValidationException(
                            id, $"field '{field}' ({type}) cannot carry a '{code}' constraint.",
                            FormDefinitionCodes.ConstraintTypeMismatch, field);
                    }
                    if (!double.TryParse(v.Param, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        throw new FormDefinitionValidationException(
                            id, $"field '{field}' constraint '{code}' needs a numeric parameter (got '{v.Param}').",
                            FormDefinitionCodes.ConstraintBadParam, field);
                    }
                    break;

                case "pattern":
                    if (isNumeric || isBoolean || isMoney)
                    {
                        throw new FormDefinitionValidationException(
                            id, $"field '{field}' ({type}) cannot carry a 'pattern' constraint.",
                            FormDefinitionCodes.ConstraintTypeMismatch, field);
                    }
                    if (string.IsNullOrEmpty(v.Param))
                    {
                        throw new FormDefinitionValidationException(
                            id, $"field '{field}' constraint 'pattern' needs a non-empty regular expression.",
                            FormDefinitionCodes.ConstraintBadParam, field);
                    }
                    try
                    {
                        // Construction-validity only; the registry's TimedPatternKeyword owns the
                        // ReDoS match-timeout control at validation time.
                        _ = new System.Text.RegularExpressions.Regex(v.Param);
                    }
                    catch (ArgumentException)
                    {
                        throw new FormDefinitionValidationException(
                            id, $"field '{field}' constraint 'pattern' is not a valid regular expression.",
                            FormDefinitionCodes.ConstraintBadPattern, field);
                    }
                    break;
            }
        }

        // Conflicting bounds — a constraint set no value could ever satisfy is authoring
        // error, not a runtime surprise.
        if (parsed.TryGetValue("minLength", out var minL) && parsed.TryGetValue("maxLength", out var maxL)
            && int.Parse(minL!, System.Globalization.CultureInfo.InvariantCulture)
               > int.Parse(maxL!, System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new FormDefinitionValidationException(
                id, $"field '{field}' has minLength > maxLength.",
                FormDefinitionCodes.ConstraintBoundsConflict, field);
        }
        if (parsed.TryGetValue("minimum", out var minN) && parsed.TryGetValue("maximum", out var maxN)
            && double.Parse(minN!, System.Globalization.CultureInfo.InvariantCulture)
               > double.Parse(maxN!, System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new FormDefinitionValidationException(
                id, $"field '{field}' has minimum > maximum.",
                FormDefinitionCodes.ConstraintBoundsConflict, field);
        }

        return parsed;
    }

    // `isRequired` defaults false for the nested (collection-row / group) call site, which enforces
    // presence via its object's own `required` array rather than the top-level `minLength:1` floor.
    private static void WritePropertySchema(Utf8JsonWriter w, FieldMetaDto? meta, Dictionary<string, string?> constraints, bool isRequired = false)
    {
        var type = meta?.Type ?? "text";

        // enum options drive the JSON-Schema enum for select/radio so a submit validates.
        // (No minLength synthesis here — the enum already rejects "" unless the author
        // deliberately listed "" as an option.)
        if ((type == "select" || type == "radio") && meta?.Options is { Count: > 0 } options)
        {
            w.WriteStartObject();
            w.WriteString("type", "string");
            w.WritePropertyName("enum");
            w.WriteStartArray();
            foreach (var o in options)
            {
                w.WriteStringValue(o);
            }
            w.WriteEndArray();
            w.WriteEndObject();
            return;
        }

        w.WriteStartObject();
        var isStringKind = false;
        if (MoneyTypes.Contains(type))
        {
            // F1 (deep review of #1683): money rides the wire as a decimal STRING (matching the
            // CurrencyField renderer, which never Number()-round-trips), so the rule engine's
            // Σ-aggregate takes the EXACT-decimal fold instead of the lossy IEEE-754 one — a
            // balanced entry with fractional cents no longer false-rejects. The pattern fences the
            // wire shape; `isStringKind` gives a REQUIRED money field the `minLength:1` floor below
            // so "" fails closed (parity with the F1-#1671 required-string corner).
            w.WriteString("type", "string");
            w.WriteString("pattern", MoneyPattern);
            isStringKind = true;
        }
        else if (NumericTypes.Contains(type))
        {
            w.WriteString("type", "number");
        }
        else if (BooleanTypes.Contains(type))
        {
            w.WriteString("type", "boolean");
        }
        else if (type == "date")
        {
            w.WriteString("type", "string");
            w.WriteString("format", "date");
            isStringKind = true;
        }
        else
        {
            // text / textarea / email / phone / url / select-without-options / unknown → free string.
            w.WriteString("type", "string");
            isStringKind = true;
        }

        // F-20: lower the validated constraint set onto the property schema — the
        // Tier-1 keywords' canonical home, enforced by the registry at submit with
        // the same stable codes + params the client mirrors.
        if (constraints.TryGetValue("minLength", out var minLength))
        {
            w.WriteNumber("minLength", int.Parse(minLength!, System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (isRequired && isStringKind)
        {
            // F1 (deep review of #1671): the registry's `required` keyword is PRESENCE-only,
            // so without a length floor a direct server submit with "" for a statically-
            // required string field is ACCEPTED while the client blocks it (the client's
            // empty-value predicate treats "" as unanswered). Emit `minLength: 1` for every
            // required string-kind field carrying no authored minLength, so both tiers fail
            // closed. Codes differ in this corner by design (client: `required`; server:
            // `minLength`) — the parity corpus pins the divergence explicitly. Numeric /
            // boolean fields are excluded ("" already fails their `type`); enum-backed
            // select/radio are excluded above (the enum already rejects "").
            w.WriteNumber("minLength", 1);
        }
        if (constraints.TryGetValue("maxLength", out var maxLength))
        {
            w.WriteNumber("maxLength", int.Parse(maxLength!, System.Globalization.CultureInfo.InvariantCulture));
        }
        if (constraints.TryGetValue("pattern", out var pattern))
        {
            w.WriteString("pattern", pattern!);
        }
        if (constraints.TryGetValue("minimum", out var minimum))
        {
            w.WriteNumber("minimum", double.Parse(minimum!, System.Globalization.CultureInfo.InvariantCulture));
        }
        if (constraints.TryGetValue("maximum", out var maximum))
        {
            w.WriteNumber("maximum", double.Parse(maximum!, System.Globalization.CultureInfo.InvariantCulture));
        }
        w.WriteEndObject();
    }
}

// ── Wire shapes (mirror the Harborline App definitionsClient TS) ─────────────────────

/// <summary>The save-definition request body (PUT). Carries the overlay + the
/// per-field authoring metadata the server synthesises the JSON Schema from.
/// <c>draft: true</c> (ticket 156) saves the revision as a Draft — registered but NOT
/// published, may be partial (zero sections), and skips the publish gates (except
/// classification admission) until a later non-draft PUT publishes the next revision.
/// Default null/absent keeps the pre-draft behavior (save = register + publish). The
/// <c>WhenWritingNull</c> ignore matters on the PACK path only: the server's PUT handler
/// never re-serializes this record, but <see cref="Data.PackProjection.PackFormDefinitionContent"/>
/// does when projecting a form back onto pinned pack content — omitting an absent
/// <c>draft</c> keeps that content byte-identical to a pre-draft export.</summary>
public sealed record SaveFormDefinitionRequest(
    [property: JsonPropertyName("overlay")] OverlayDto Overlay,
    [property: JsonPropertyName("fieldsMeta")] IReadOnlyDictionary<string, FieldMetaDto>? FieldsMeta,
    [property: JsonPropertyName("draft"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Draft = null);

/// <summary>Per-field schema-synthesis metadata the overlay intentionally omits
/// (type / required / validations / enum values live below the overlay, on the
/// JSON Schema).</summary>
public sealed record FieldMetaDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("options")] IReadOnlyList<string>? Options,
    // F-20: the full authored validation set (all six codes + params) — lowered onto
    // the synthesized property schema. Default null keeps the record back-compat for
    // positional construction and a pre-F-20 wire body.
    [property: JsonPropertyName("validations"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FieldValidationDto>? Validations = null);

/// <summary>One authored validation constraint on the wire (F-20): a STABLE code
/// (<c>required</c> / <c>minLength</c> / <c>maxLength</c> / <c>pattern</c> /
/// <c>minimum</c> / <c>maximum</c>) + its optional parameter. Mirrors the TS
/// <c>BuilderValidation</c>.</summary>
public sealed record FieldValidationDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("param"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Param = null);

/// <summary>The 200 response after a successful save — the saved id + version.</summary>
public sealed record SaveFormDefinitionResponse(
    [property: JsonPropertyName("formId")] string FormId,
    [property: JsonPropertyName("version")] string Version);

/// <summary>A list entry for the tenant's definitions (GET list). Ticket 153 (L1351/L1352):
/// the envelope's <c>cascadeLayer</c> travels on the wire — the same posture as
/// <see cref="DataExchangeDefinitionRoutes"/> — so a client renders provenance instead of
/// fabricating it.</summary>
public sealed record FormDefinitionSummaryDto(
    [property: JsonPropertyName("formId")] string FormId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InternationalizedTextDto? Title,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("cascadeLayer")] string CascadeLayer,
    // Draft discoverability (ticket 156 review): the row's lifecycle status ("Published", or
    // "Draft" for a draft-only form surfaced via ?includeDrafts), plus — on a published row —
    // the version of a NEWER draft when one exists, so a client can detect it instead of
    // silently minting its next draft off the stale published head.
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("latestDraftVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LatestDraftVersion = null)
{
    public static FormDefinitionSummaryDto From(FormDefinition def, string? latestDraftVersion = null) => new(
        def.Id.Value,
        def.Version.ToString(),
        InternationalizedTextDto.From(def.Overlay.Title),
        def.UpdatedAt.UtcDateTime.ToString("O"),
        def.Envelope.CascadeLayer.ToString(),
        def.Status.ToString(),
        latestDraftVersion);
}

/// <summary>The authoring view of a definition (GET by id) — the overlay the
/// builder reconstructs its editing model from. The Harborline App reads
/// <c>layout</c> / <c>fieldPlacement</c> STRAIGHT OFF this (no client re-projection).</summary>
public sealed record FormDefinitionDto(
    [property: JsonPropertyName("formId")] string FormId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("overlay")] OverlayDto Overlay,
    // Ticket 153: envelope provenance travels on the wire (list + detail).
    [property: JsonPropertyName("cascadeLayer")] string CascadeLayer,
    // Draft discoverability (ticket 156 review): the served revision's lifecycle status —
    // "Published" for the head, "Draft" when GET-by-id serves a draft-only form (or when
    // /versions/{v} serves a draft revision) — plus, on a published head, the version of a
    // NEWER draft when one exists (see FormDefinitionSummaryDto).
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("latestDraftVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LatestDraftVersion = null)
{
    public static FormDefinitionDto From(FormDefinition def, string? latestDraftVersion = null) => new(
        def.Id.Value,
        def.Version.ToString(),
        OverlayDto.From(def.Overlay),
        def.Envelope.CascadeLayer.ToString(),
        def.Status.ToString(),
        latestDraftVersion);
}

/// <summary>One revision in a form's version history (GET .../versions) — F-22, item 7.
/// Enriches the plain summary with lifecycle status + author + the restore-provenance link
/// (<c>derivedFrom</c> = the version this one was restored from, if any). It also states that
/// revisions synchronize to peers and are not an isolated staging surface.</summary>
public sealed record FormVersionSummaryDto(
    [property: JsonPropertyName("formId")] string FormId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("owner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Owner,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("derivedFrom"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DerivedFrom,
    [property: JsonPropertyName("syncsToPeers")] bool SyncsToPeers,
    [property: JsonPropertyName("safeForStaging")] bool SafeForStaging,
    // Ticket 153 review: every sibling pillar emits cascadeLayer on version rows too.
    [property: JsonPropertyName("cascadeLayer")] string CascadeLayer)
{
    public static FormVersionSummaryDto From(FormDefinition def) => new(
        def.Id.Value,
        def.Version.ToString(),
        def.Status.ToString(),
        def.Owner.ToString(),
        def.UpdatedAt.UtcDateTime.ToString("O"),
        def.CreatedAt.UtcDateTime.ToString("O"),
        def.Lineage?.ParentVersion.ToString(),
        SyncsToPeers: true,
        SafeForStaging: false,
        CascadeLayer: def.Envelope.CascadeLayer.ToString());
}

/// <summary>The restore request body (POST .../restore) — the version to restore as a new
/// draft. Mirrors the Harborline App <c>restoreVersion</c> call.</summary>
public sealed record RestoreVersionRequest(
    [property: JsonPropertyName("version")] string Version);

/// <summary>The Harborline overlay on the wire (authoring side).</summary>
public sealed record OverlayDto(
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, FieldOverlayDto> Fields,
    [property: JsonPropertyName("sections")] IReadOnlyList<OverlaySectionDto> Sections,
    [property: JsonPropertyName("rules")] IReadOnlyList<RuleDto>? Rules,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InternationalizedTextDto? Title,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InternationalizedTextDto? Description,
    // F-14: the wizard-page grain. Both default null so a pageless wire body (and any
    // positional construction) stays byte-identical to the pre-F-14 shape.
    [property: JsonPropertyName("pages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FormPageDto>? Pages = null,
    [property: JsonPropertyName("wizard"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WizardSettingsDto? Wizard = null,
    // F-20: the async validation checks. Default null ⇒ byte-identical pre-F-20 wire.
    [property: JsonPropertyName("asyncChecks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AsyncCheckDto>? AsyncChecks = null,
    [property: JsonPropertyName("aspects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AspectOverlayDto? Aspects = null)
{
    public static OverlayDto From(HarborlineOverlay o) => new(
        o.Fields.ToDictionary(kv => kv.Key, kv => FieldOverlayDto.From(kv.Value)),
        o.Sections.Select(OverlaySectionDto.From).ToList(),
        o.Rules.Select(RuleDto.From).ToList(),
        InternationalizedTextDto.From(o.Title),
        InternationalizedTextDto.From(o.Description),
        // F-14: project the pages so a saved paged form reloads with its wizard
        // structure intact (the LOAD half of the persist round-trip).
        o.Pages is { Count: > 0 } ? o.Pages.Select(FormPageDto.From).ToList() : null,
        WizardSettingsDto.From(o.Wizard),
        // F-20: project the async checks (the LOAD half of the persist round-trip).
        o.AsyncChecks is { Count: > 0 } ? o.AsyncChecks.Select(AsyncCheckDto.From).ToList() : null,
        AspectOverlayDto.From(o.Aspects));
}

/// <summary>A wizard page on the wire (F-14). Mirrors the TS <c>FormPage</c>:
/// ordered section ids + an optional stringified SPINE-1 visibility guard +
/// (F-20) optional page-check rule ids.</summary>
public sealed record FormPageDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] InternationalizedTextDto? Title,
    [property: JsonPropertyName("sections")] IReadOnlyList<string> Sections,
    [property: JsonPropertyName("visibleWhen"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VisibleWhen = null,
    [property: JsonPropertyName("checks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Checks = null)
{
    public static FormPageDto From(FormPage p) => new(
        p.Id,
        InternationalizedTextDto.From(p.Title)!,
        p.Sections,
        p.VisibleWhen,
        p.Checks is { Count: > 0 } ? p.Checks : null);

    public FormPage ToModel() => new(
        Id,
        // F4 (deep review of #1664): a PUT body missing `title` deserializes to null —
        // fall back to the page id (the extension's empty-dto fallback) rather than
        // trusting the non-null declaration and risking a 500.
        Title.ToModel(Id),
        Sections ?? Array.Empty<string>(),
        VisibleWhen,
        Checks is { Count: > 0 } ? Checks : null);
}

/// <summary>A lookup-backed async validation check on the wire (F-20). Mirrors the
/// TS <c>AsyncValidationCheck</c> — config only (connector key + field wiring +
/// stable codes), never imperative code.</summary>
public sealed record AsyncCheckDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("connector")] string Connector,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("failCode")] string FailCode,
    [property: JsonPropertyName("inputs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Inputs = null,
    [property: JsonPropertyName("debounceMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DebounceMs = null,
    // SPINE-2 (item 6): the author's acknowledgment that this check may feed sensitively-classified
    // fields to its connector. Absent/false ⇒ admission rejects a sensitive-fed check fail-closed.
    [property: JsonPropertyName("allowsSensitiveInputs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AllowsSensitiveInputs = null)
{
    public static AsyncCheckDto From(AsyncValidationCheck c) => new(
        c.Id, c.Connector, c.Field, c.FailCode,
        c.Inputs is { Count: > 0 } ? c.Inputs : null,
        c.DebounceMs,
        c.AllowsSensitiveInputs ? true : null);

    public AsyncValidationCheck ToModel() => new(
        Id ?? string.Empty,
        Connector ?? string.Empty,
        Field ?? string.Empty,
        FailCode ?? string.Empty,
        Inputs is { Count: > 0 } ? Inputs : null,
        DebounceMs,
        AllowsSensitiveInputs ?? false);
}

/// <summary>Wizard chrome settings on the wire (F-14). Mirrors the TS
/// <c>WizardSettings</c>; <c>confirmation</c> defaults true (absent ⇒ shown).</summary>
public sealed record WizardSettingsDto(
    [property: JsonPropertyName("review")] bool Review = false,
    [property: JsonPropertyName("confirmation")] bool Confirmation = true,
    [property: JsonPropertyName("confirmationMessage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InternationalizedTextDto? ConfirmationMessage = null,
    [property: JsonPropertyName("onSuccess"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OnSuccessDto? OnSuccess = null)
{
    public static WizardSettingsDto? From(WizardSettings? w) => w is null
        ? null
        : new(w.Review, w.Confirmation, InternationalizedTextDto.From(w.ConfirmationMessage), OnSuccessDto.From(w.OnSuccess));

    public WizardSettings ToModel() => new(Review, Confirmation, ConfirmationMessage?.ToModel(), OnSuccess?.ToModel());
}

/// <summary>Declarative on-success config on the wire (F-14) — config fields only;
/// the HOST consumes them (redirect / host-callback), never the definition.</summary>
public sealed record OnSuccessDto(
    [property: JsonPropertyName("redirectUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RedirectUrl = null,
    [property: JsonPropertyName("hostCallback"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HostCallback = null)
{
    public static OnSuccessDto? From(OnSuccessConfig? c) => c is null ? null : new(c.RedirectUrl, c.HostCallback);

    public OnSuccessConfig ToModel() => new(RedirectUrl, HostCallback);
}

/// <summary>A field overlay on the wire.</summary>
public sealed record FieldOverlayDto(
    [property: JsonPropertyName("label")] InternationalizedTextDto Label,
    [property: JsonPropertyName("helpText"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InternationalizedTextDto? HelpText,
    [property: JsonPropertyName("controlHint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ControlHint,
    [property: JsonPropertyName("piiSensitivity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PiiSensitivity,
    // F-17: the per-control config (currency code, file accept / multiple). Absent ⇒
    // omitted (byte-identical to a pre-F-17 field). Default null keeps the record
    // back-compat for any positional construction.
    [property: JsonPropertyName("config"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FieldConfigDto? Config = null,
    // SPINE-2 (item 6): the field-grain classification aspect. Absent ⇒ byte-identical.
    [property: JsonPropertyName("aspects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AspectOverlayDto? Aspects = null,
    [property: JsonPropertyName("readRoles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ReadRoles = null,
    [property: JsonPropertyName("writeRoles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? WriteRoles = null,
    [property: JsonPropertyName("readStandings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ReadStandings = null,
    [property: JsonPropertyName("writeStandings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? WriteStandings = null)
{
    public static FieldOverlayDto From(FieldOverlay f) => new(
        InternationalizedTextDto.From(f.Label)!,
        InternationalizedTextDto.From(f.HelpText),
        f.ControlHint,
        f.PiiSensitivity == Harborline.Api.Foundation.Forms.Models.PiiSensitivity.Sensitive ? "Sensitive" : "None",
        FieldConfigDto.From(f.Config),
        AspectOverlayDto.From(f.Aspects),
        f.FieldReadRoles,
        f.FieldWriteRoles,
        f.FieldReadStandings?.Select(item => item.Name).ToArray(),
        f.FieldWriteStandings?.Select(item => item.Name).ToArray());
}

/// <summary>The SPINE-2 aspect overlay on the wire (ADR 0140 D2 — item 6). Carries the
/// CLASSIFICATION aspect (the tags that drive policy); the access / lifecycle / discovery
/// aspect editors are the follow-up (#21) and extend this DTO additively when they land.
/// Absent ⇒ byte-identical to a pre-SPINE-2 grain. Mirrors the TS <c>AspectOverlay</c>.</summary>
public sealed record AspectOverlayDto(
    [property: JsonPropertyName("classification"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClassificationAspectDto? Classification = null,
    [property: JsonPropertyName("access"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AccessGateDto? Access = null)
{
    /// <summary>Project a stored overlay onto the wire; null when it carries no classification
    /// tags (so an untagged grain stays byte-identical — the overlay key is omitted).</summary>
    public static AspectOverlayDto? From(AspectOverlay? a) => a is null
        ? null
        : new AspectOverlayDto(
            a.Classification is { Tags.Count: > 0 } c ? ClassificationAspectDto.From(c) : null,
            AccessGateDto.From(a.Access));

    /// <summary>Reconstruct the canonical overlay; null when no classification tags are carried.</summary>
    public AspectOverlay? ToModel() => Classification is null && Access is null
        ? null
        : new AspectOverlay(
            Classification: Classification is { Tags.Count: > 0 } c ? c.ToModel() : null,
            Access: Access?.ToAccessAspect());
}

/// <summary>Typed role/standing lanes shared by section and aspect access DTOs.</summary>
public sealed record AccessGateDto(
    [property: JsonPropertyName("readRoles")] IReadOnlyList<string> ReadRoles,
    [property: JsonPropertyName("writeRoles")] IReadOnlyList<string> WriteRoles,
    [property: JsonPropertyName("readConditionExpression"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReadConditionExpression = null,
    [property: JsonPropertyName("readStandings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ReadStandings = null,
    [property: JsonPropertyName("writeStandings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? WriteStandings = null)
{
    public static AccessGateDto From(SectionAccess access) => new(
        access.ReadRoles,
        access.WriteRoles,
        access.ReadConditionExpression,
        access.ReadStandings?.Select(item => item.Name).ToArray(),
        access.WriteStandings?.Select(item => item.Name).ToArray());

    public static AccessGateDto? From(AccessAspect? access) => access is null
        ? null
        : new(
            access.ReadRoles ?? [],
            access.WriteRoles ?? [],
            access.ReadConditionExpression,
            access.ReadStandings?.Select(item => item.Name).ToArray(),
            access.WriteStandings?.Select(item => item.Name).ToArray());

    public SectionAccess ToSectionAccess() => new(
        ReadRoles ?? [],
        WriteRoles ?? [],
        ReadConditionExpression,
        ToStandings(ReadStandings, "sections.access.readStandings"),
        ToStandings(WriteStandings, "sections.access.writeStandings"));

    public AccessAspect ToAccessAspect() => new(
        ReadRoles,
        WriteRoles,
        ReadConditionExpression,
        ToStandings(ReadStandings, "aspects.access.readStandings"),
        ToStandings(WriteStandings, "aspects.access.writeStandings"));

    internal static IReadOnlyList<RecordStandingReference>? ToStandings(
        IReadOnlyList<string>? values,
        string field) => values?.Select(value => RecordStandingReference.Parse(value, field)).ToArray();
}

/// <summary>The classification aspect on the wire — the open-vocab tags. Mirrors the TS
/// <c>ClassificationAspect</c>.</summary>
public sealed record ClassificationAspectDto(
    [property: JsonPropertyName("tags")] IReadOnlyList<TagDto> Tags)
{
    public static ClassificationAspectDto From(ClassificationAspect c) =>
        new(c.Tags.Select(TagDto.From).ToList());

    public ClassificationAspect ToModel() =>
        new(Tags.Where(t => !string.IsNullOrWhiteSpace(t.System) && !string.IsNullOrWhiteSpace(t.Code))
             .Select(t => t.ToModel()).ToList());
}

/// <summary>A classification tag (CodeableConcept, ADR 0056) on the wire — <c>(system, code)</c>
/// is the identity; <c>display</c> is advisory. Mirrors the TS <c>Tag</c>.</summary>
public sealed record TagDto(
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("display"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Display = null)
{
    public static TagDto From(Tag t) => new(t.System, t.Code, t.Display);

    public Tag ToModel() => new(System ?? string.Empty, Code ?? string.Empty, Display);
}

/// <summary>A field's per-control config on the wire (F-17). Mirrors the TS
/// <c>FieldConfig</c>; each key omitted when null so the wire carries only the
/// settings a field actually sets.</summary>
public sealed record FieldConfigDto(
    [property: JsonPropertyName("currencyCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrencyCode = null,
    [property: JsonPropertyName("accept"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Accept = null,
    [property: JsonPropertyName("multiple"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Multiple = null)
{
    public static FieldConfigDto? From(FieldConfig? c) => c is null ? null : new(c.CurrencyCode, c.Accept, c.Multiple);

    public FieldConfig ToModel() => new(CurrencyCode, Accept, Multiple);
}

/// <summary>A section on the wire (authoring side — carries layout/placement +,
/// ADR 0055 Rev 7, the recursive item tree).</summary>
public sealed record OverlaySectionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] InternationalizedTextDto Title,
    [property: JsonPropertyName("fields")] IReadOnlyList<string> Fields,
    [property: JsonPropertyName("layout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SectionLayoutDto? Layout,
    [property: JsonPropertyName("fieldPlacement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, FieldPlacementDto>? FieldPlacement,
    [property: JsonPropertyName("items"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FormItemDto>? Items,
    // SPINE-2 (item 6): the section-grain classification aspect. Absent ⇒ byte-identical.
    [property: JsonPropertyName("aspects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AspectOverlayDto? Aspects = null,
    [property: JsonPropertyName("access"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AccessGateDto? Access = null)
{
    public static OverlaySectionDto From(FormSection s) => new(
        s.Id,
        InternationalizedTextDto.From(s.Title)!,
        s.Fields,
        SectionLayoutDto.From(s.Layout),
        s.FieldPlacement?.ToDictionary(kv => kv.Key, kv => FieldPlacementDto.From(kv.Value)),
        // ADR 0055 Rev 7: project the nested item tree so a saved nested form reloads
        // with its structure intact (the LOAD half of the persist round-trip).
        s.Items is { Count: > 0 } ? s.Items.Select(FormItemDto.From).ToList() : null,
        AspectOverlayDto.From(s.Aspects),
        AccessGateDto.From(s.Access));
}

/// <summary>
/// A recursive form-item node on the wire (authoring side — ADR 0055 Rev 7 nested
/// sub-form items). The .NET <see cref="FormItem"/> discriminated union on the wire:
/// <c>kind</c> is the lowercase string union (<c>field</c> / <c>group</c> /
/// <c>collection</c>) hand-mapped to <see cref="FormItemKind"/> BOTH directions — the
/// same enum-as-string precedent <see cref="SectionLayoutDto"/> sets, so the tree does
/// NOT depend on a <c>JsonStringEnumConverter</c> being wired on the request path.
/// </summary>
public sealed record FormItemDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("items"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FormItemDto>? Items,
    [property: JsonPropertyName("cardinality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CardinalityDto? Cardinality,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InternationalizedTextDto? Title,
    // D4 (ADR 0135 amendment 2026-07-01): a `reference` node carries the reusable-unit ref
    // instead of inline children — the subtree resolves from the unit (reuse-by-reference).
    [property: JsonPropertyName("reference"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReusableUnitRefDto? Reference,
    // F-23: a `content` block's nodes / an `action` block's declarative config / a group
    // zone's layout+placement intents. All omitted-when-null ⇒ a pre-F-23 tree stays
    // byte-identical on the wire.
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ContentNodeDto>? Content = null,
    [property: JsonPropertyName("action"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FormActionDto? Action = null,
    [property: JsonPropertyName("layout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SectionLayoutDto? Layout = null,
    [property: JsonPropertyName("placement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, FieldPlacementDto>? Placement = null,
    // F-24 (item 5): a `collection`'s tabular presentation (columns + totals). Omitted-when-null
    // ⇒ a pre-F-24 tree stays byte-identical on the wire.
    [property: JsonPropertyName("table"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CollectionTableConfigDto? Table = null,
    // SPINE-2 (item 6): a container's (group/collection) classification aspect. Omitted-when-null
    // ⇒ byte-identical on the wire; ignored on non-container kinds.
    [property: JsonPropertyName("aspects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AspectOverlayDto? Aspects = null)
{
    public static FormItemDto From(FormItem item) => new(
        FormDefinitionDtoExtensions.KindToWire(item.Kind),
        item.Key,
        item.Items is { Count: > 0 } ? item.Items.Select(From).ToList() : null,
        CardinalityDto.From(item.Cardinality),
        InternationalizedTextDto.From(item.Title),
        ReusableUnitRefDto.From(item.Reference),
        item.Content is { Count: > 0 } ? item.Content.Select(ContentNodeDto.From).ToList() : null,
        FormActionDto.From(item.Action),
        SectionLayoutDto.From(item.Layout),
        item.Placement?.ToDictionary(kv => kv.Key, kv => FieldPlacementDto.From(kv.Value)),
        CollectionTableConfigDto.From(item.Table),
        AspectOverlayDto.From(item.Aspects));
}

/// <summary>A <see cref="FormItemKind.Collection"/>'s tabular presentation on the wire (F-24 —
/// grid/table input, item 5). Mirrors the TS <c>CollectionTableConfig</c> — columns + totals,
/// presentation only. The closed intent-token sets + total-key membership are enforced by the
/// store's admission validation (stable 422 codes), keeping the wire mapping thin.</summary>
public sealed record CollectionTableConfigDto(
    [property: JsonPropertyName("columns"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, CollectionColumnDto>? Columns = null,
    [property: JsonPropertyName("totals"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Totals = null)
{
    public static CollectionTableConfigDto? From(CollectionTableConfig? t) => t is null
        ? null
        : new(
            t.Columns is { Count: > 0 } ? t.Columns.ToDictionary(kv => kv.Key, kv => CollectionColumnDto.From(kv.Value)) : null,
            t.Totals is { Count: > 0 } ? t.Totals : null);

    public CollectionTableConfig ToModel() => new(
        Columns is { Count: > 0 } ? Columns.ToDictionary(kv => kv.Key, kv => kv.Value.ToModel()) : null,
        Totals is { Count: > 0 } ? Totals : null);
}

/// <summary>One column's presentation config on the wire (F-24). Mirrors the TS
/// <c>CollectionColumn</c> — bounded width / align intent tokens (reusing the F-23 vocab).</summary>
public sealed record CollectionColumnDto(
    [property: JsonPropertyName("width"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Width = null,
    [property: JsonPropertyName("align"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Align = null)
{
    public static CollectionColumnDto From(CollectionColumn c) => new(c.Width, c.Align);

    public CollectionColumn ToModel() => new(Width, Align);
}

/// <summary>One node of a static content block on the wire (F-23). Mirrors the TS
/// <c>ContentNode</c> — a closed <c>kind</c> (<c>heading</c>/<c>paragraph</c>) + localized
/// PLAIN text (+ heading level intent). The kind string passes through verbatim; the closed
/// set is enforced by the store's admission validation with a stable 422 code (fail-closed),
/// not by wire deserialization.</summary>
public sealed record ContentNodeDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] InternationalizedTextDto? Text,
    [property: JsonPropertyName("level"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Level = null)
{
    public static ContentNodeDto From(ContentNode n) => new(
        n.Kind, InternationalizedTextDto.From(n.Text), n.Level);

    public ContentNode ToModel() => new(
        Kind ?? string.Empty,
        Text.ToModel(string.Empty),
        Level);
}

/// <summary>An action block's declarative config on the wire (F-23). Mirrors the TS
/// <c>FormActionConfig</c> — a closed <c>kind</c> (<c>open-url</c>/<c>scroll-to-section</c>)
/// + label + one target. Config only, never code; the closed set + URL scheme + section
/// target are enforced by admission validation with stable 422 codes.</summary>
public sealed record FormActionDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] InternationalizedTextDto? Label,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonPropertyName("sectionId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SectionId = null)
{
    public static FormActionDto? From(FormActionConfig? a) => a is null
        ? null
        : new(a.Kind, InternationalizedTextDto.From(a.Label), a.Url, a.SectionId);

    public FormActionConfig ToModel() => new(
        Kind ?? string.Empty,
        Label.ToModel(string.Empty),
        Url,
        SectionId);
}

/// <summary>A D4 reusable-unit reference on the wire (<c>{ unitId, version: { pinnedVersion } }</c>);
/// mirrors the TS <c>ReusableUnitRef</c>. <c>pinnedVersion</c> null ⇒ latest-published.</summary>
public sealed record ReusableUnitRefDto(
    [property: JsonPropertyName("unitId")] string UnitId,
    [property: JsonPropertyName("version")] ReusableUnitVersionSelectorDto Version)
{
    public static ReusableUnitRefDto? From(ReusableUnitRef? r) => r is null
        ? null
        : new(r.UnitId, new ReusableUnitVersionSelectorDto(r.Version.PinnedVersion?.ToString()));
}

/// <summary>A D4 version selector on the wire — <c>pinnedVersion</c> is the canonical
/// <c>"{major}.{minor}.{patch}"</c> string, or null to track the latest published version.</summary>
public sealed record ReusableUnitVersionSelectorDto(
    [property: JsonPropertyName("pinnedVersion")] string? PinnedVersion);

/// <summary>A <see cref="FormItemKind.Collection"/> item's instance-count bounds on the
/// wire. Mirrors the TS <c>Cardinality</c> (<c>{ min, max? }</c>); <c>max</c> absent /
/// null ⇒ unbounded (still evaluation-capped).</summary>
public sealed record CardinalityDto(
    [property: JsonPropertyName("min")] int Min,
    [property: JsonPropertyName("max"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Max)
{
    public static CardinalityDto? From(Cardinality? c) => c is null ? null : new(c.Min, c.Max);
}

/// <summary>A rule on the wire.</summary>
public sealed record RuleDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("scopeTarget")] string ScopeTarget,
    [property: JsonPropertyName("expression")] string Expression,
    [property: JsonPropertyName("action")] string Action)
{
    public static RuleDto From(RuleDefinition r) => new(
        r.Id, r.Tier.ToString(), r.Scope.ToString(), r.ScopeTarget, r.Expression, r.Action.ToString());
}

// ── DTO → model helpers ────────────────────────────────────────────────────────

internal static class FormDefinitionDtoExtensions
{
    public static InternationalizedText ToModel(this InternationalizedTextDto? dto, string fallback)
    {
        if (dto is null || dto.Values is null || dto.Values.Count == 0)
        {
            return new InternationalizedText("en", new Dictionary<string, string> { ["en"] = fallback });
        }
        return new InternationalizedText(
            string.IsNullOrWhiteSpace(dto.DefaultLocale) ? "en" : dto.DefaultLocale,
            new Dictionary<string, string>(dto.Values));
    }

    public static InternationalizedText? ToModel(this InternationalizedTextDto? dto)
        => dto is null ? null : dto.ToModel("");

    public static SectionLayout ToModel(this SectionLayoutDto dto) => new(
        Kind: dto.Kind switch
        {
            "flex" => SectionLayoutKind.Flex,
            "grid" => SectionLayoutKind.Grid,
            _ => SectionLayoutKind.Stack,
        },
        Direction: dto.Direction == "column" ? FlexDirection.Column : FlexDirection.Row,
        Wrap: dto.Wrap == "nowrap" ? FlexWrap.NoWrap : FlexWrap.Wrap,
        Columns: dto.Columns == 0 ? 2 : dto.Columns,
        Gap: dto.Gap,
        // F-23 intents pass through verbatim — the closed token sets are enforced by the
        // store's admission validation (stable 422 codes), keeping the wire mapping thin.
        CollapseBelow: dto.CollapseBelow,
        Density: dto.Density,
        Align: dto.Align);

    public static FieldPlacement ToModel(this FieldPlacementDto dto)
        => new(ColSpan: dto.ColSpan == 0 ? 1 : dto.ColSpan, Grow: dto.Grow, Width: dto.Width, Align: dto.Align);

    // ── ADR 0055 Rev 7: recursive item-tree DTO ↔ model ─────────────────────────

    /// <summary>Reconstructs a wire <see cref="FormItemDto"/> (and its children,
    /// recursively) into the canonical <see cref="FormItem"/>. The reconstructed tree
    /// is hung on <see cref="FormSection.Items"/> so <c>ValidateOverlayOrThrow</c> at
    /// <c>RegisterAsync</c> exercises the fail-closed <see cref="FormTreeLimits"/> bounds
    /// on this WIRED path.</summary>
    public static FormItem ToModel(this FormItemDto dto) => new(
        Kind: WireToKind(dto.Kind),
        Key: dto.Key,
        Items: dto.Items is { Count: > 0 }
            ? dto.Items.Select(i => i.ToModel()).ToList()
            : null,
        Cardinality: dto.Cardinality?.ToModel(),
        Title: dto.Title?.ToModel(),
        Reference: dto.Reference?.ToModel(),
        // F-23: block payloads + group-zone intents (validated fail-closed at RegisterAsync).
        Content: dto.Content is { Count: > 0 } ? dto.Content.Select(n => n.ToModel()).ToList() : null,
        Action: dto.Action?.ToModel(),
        Layout: dto.Layout?.ToModel(),
        Placement: dto.Placement?.ToDictionary(kv => kv.Key, kv => kv.Value.ToModel()),
        // F-24: a collection's tabular presentation (validated fail-closed at RegisterAsync —
        // table-on-non-collection + unknown tokens + unknown total keys are 422).
        Table: dto.Table?.ToModel(),
        // SPINE-2 (item 6): the container-grain classification aspect (the resolver walks it as a
        // grain between the section and each nested field; unknown-kind tags are 422 at admission).
        Aspects: dto.Aspects?.ToModel());

    public static Cardinality ToModel(this CardinalityDto dto) => new(dto.Min, dto.Max);

    /// <summary>Reconstructs a wire <see cref="ReusableUnitRefDto"/> into the canonical
    /// <see cref="ReusableUnitRef"/> (D4). A null <c>pinnedVersion</c> ⇒ latest-published.</summary>
    public static ReusableUnitRef ToModel(this ReusableUnitRefDto dto) => new(
        new ReusableUnitId(dto.UnitId),
        string.IsNullOrEmpty(dto.Version?.PinnedVersion)
            ? ReusableUnitVersionSelector.LatestPublished
            : ReusableUnitVersionSelector.Pin(SemanticVersion.Parse(dto.Version.PinnedVersion)));

    /// <summary>Maps a <see cref="FormItemKind"/> onto its lowercase wire string (the
    /// TS discriminated-union tag).</summary>
    internal static string KindToWire(FormItemKind kind) => kind switch
    {
        FormItemKind.Field => "field",
        FormItemKind.Group => "group",
        FormItemKind.Collection => "collection",
        FormItemKind.Reference => "reference",
        FormItemKind.Content => "content",
        FormItemKind.Action => "action",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown form-item kind."),
    };

    /// <summary>Maps the lowercase wire string onto <see cref="FormItemKind"/>. Throws
    /// <see cref="ArgumentException"/> on an unknown tag (⇒ the PUT handler returns 400).</summary>
    internal static FormItemKind WireToKind(string kind) => kind switch
    {
        "field" => FormItemKind.Field,
        "group" => FormItemKind.Group,
        "collection" => FormItemKind.Collection,
        "reference" => FormItemKind.Reference,
        "content" => FormItemKind.Content,
        "action" => FormItemKind.Action,
        _ => throw new ArgumentException($"unknown form-item kind '{kind}'.", nameof(kind)),
    };
}
