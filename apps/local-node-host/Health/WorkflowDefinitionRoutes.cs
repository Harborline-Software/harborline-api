using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// WF-KEY (W-7 / G5) — node-local workflow-DEFINITION authoring routes (the workflow
/// BUILDER persistence surface). The process analog of <see cref="FormDefinitionRoutes"/>:
/// where those SAVE + LOAD + LIST a <c>FormDefinition</c>, these do the same for a
/// <see cref="WorkflowDefinition"/> a tenant admin authors in the Harborline App workflow builder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes (all under <see cref="RouteBase"/>):</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/workflows/definitions</c> — lists the tenant's current
///     published definitions (key + version + name) for the builder's "open a workflow" surface.</item>
///   <item><c>GET  /api/local-node/workflows/definitions/{key}</c> — loads the current published
///     authoring view (the full authored definition JSON the builder reconstructs from).</item>
///   <item><c>PUT  /api/local-node/workflows/definitions/{key}</c> — saves the authored definition:
///     runs the fail-closed <see cref="IWorkflowAdmissionValidator"/> on the derived model, then
///     persists. Re-saving an existing key mints the next patch version (definitions are immutable
///     per <c>(key, version)</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>STORAGE, not execution.</b> A definition persisted here is authored + admitted + durable; it
/// is NOT executed. The general data-driven interpreter (ADR 0135 A1) stays gated on the broker-PEP
/// (ADR 0143). Persisting an authored+admitted definition is safe and ungated — this route builds
/// exactly that and nothing more.
/// </para>
/// <para>
/// <b>Fail-closed admission at persist.</b> The PUT derives the lean
/// <see cref="WorkflowDefinition"/> admission model from the wire body and hands it to
/// <see cref="IWorkflowDefinitionStore.RegisterAsync"/>, which runs the shipped
/// <see cref="WorkflowAdmissionValidator"/> (registry-DERIVED classification, ADR 0143 / #1638 —
/// the authored label is a non-authoritative assertion) BEFORE the store write. An inadmissible
/// definition (an unclassified action, or a CP action reachable from an autonomous trigger with no
/// interposed human-task) is REJECTED with HTTP 422 carrying the stable
/// <see cref="WorkflowAdmissionCodes"/> code, and nothing is persisted.
/// </para>
/// <para>
/// <b>Tenant-scoping (INV-S1).</b> Exactly as <see cref="FormDefinitionRoutes"/>: the Harborline App sends
/// NO tenant id and NO principal. The route resolves the tenant from the active team
/// (<see cref="NodeTenant.Resolve"/>) server-side and the store enforces the boundary on every
/// lookup/listing. A definition saved under tenant A is invisible to tenant B. The route also owns
/// the id (<c>{key}</c> route param) + the minted version — the client cannot forge either.
/// </para>
/// <para>
/// <b>Round-trip fidelity.</b> The full authored JSON is persisted verbatim (with the server tenant +
/// minted version), so a load returns the definition the builder authored intact — including the
/// state display labels + title the lean admission model does not carry. (Trigger/action display
/// labels are builder-only chrome absent from the canonical <c>WorkflowDefinition</c> contract; the
/// builder re-derives them from ids on reload.)
/// </para>
/// </remarks>
public static class WorkflowDefinitionRoutes
{
    /// <summary>Canonical route base for the node-local workflow-definition authoring surface.</summary>
    public const string RouteBase = "/api/local-node/workflows/definitions";

    /// <summary>
    /// Maps the definition-authoring routes onto <paramref name="app"/>, closing over the
    /// workflow-definition store + the active-team accessor from the OUTER host container (resolved
    /// here, not via <c>[FromServices]</c>, because the routes map onto the shared inner
    /// <c>WebApplication</c> whose provider is a separate container — the same bug-2849 workaround the
    /// forms routes use).
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        AuthorizedWorkflowDefinitionLifecycle store,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeTeam);

        // GET /api/local-node/workflows/definitions — list the tenant's current published definitions.
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var summaries = new List<WorkflowDefinitionSummaryDto>();
            await foreach (var record in store.ListByTenantAsync(tenant, ct).ConfigureAwait(false))
            {
                if (record.Status != WorkflowDefinitionStatus.Published)
                {
                    continue;
                }
                summaries.Add(WorkflowDefinitionSummaryDto.From(record));
            }

            return Results.Ok(summaries);
        });

        // GET /api/local-node/workflows/definitions/{key} — load the authored definition (verbatim).
        app.MapGet($"{RouteBase}/{{key}}", async (string key, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var record = await store.GetCurrentPublishedAsync(
                new DefinitionAddress(tenant, key), ct).ConfigureAwait(false);
            if (record is null)
            {
                // INV-S1: not-found / cross-tenant are indistinguishable.
                return Results.NotFound(new { error = $"No published workflow definition '{key}' for this tenant." });
            }

            // The authored contracts WorkflowDefinition, verbatim — the builder reconstructs from it.
            return Results.Ok(record.Authored);
        });

        // PUT /api/local-node/workflows/definitions/{key} — save the authored definition.
        app.MapPut($"{RouteBase}/{{key}}", async (string key, HttpContext http, CancellationToken ct) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var at = timeProvider.GetUtcNow();
            var authority = new AuthorizationWriteContext(
                new ActorId(NodeCallerParty.Resolve(http).Value),
                tenant,
                at);
            AuthorizedWorkflowDefinitionLifecycle.WriteAuthority decision;
            try
            {
                decision = await store.DecideAsync(key, authority, ct).ConfigureAwait(false);
            }
            catch (AuthorizationDeniedException denial)
            {
                // Ticket 214 slice 2: the lifecycle authorizes INSIDE the store, so this route meets a
                // refusal as an exception rather than as a guard verdict. Its message never reaches the
                // wire — the decision it carries is rendered through the node's one read-filtered refusal
                // renderer, and the classified reading goes to the audit row.
                return await RequestAuthorization.RefusedAsync(http, denial, ct).ConfigureAwait(false);
            }

            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(
                    http.Request.Body, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Malformed request body: {ex.Message}" });
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { error = "Request body must be a workflow definition object." });
            }

            // The next version: bump the patch of the current published revision, else 1.0.0.
            var current = await store.GetCurrentPublishedAsync(
                new DefinitionAddress(tenant, key), ct).ConfigureAwait(false);
            var version = current is null ? "1.0.0" : BumpPatch(current.Version);

            // Normalize the sole authored input with the server-owned key + tenant + minted version +
            //     published status, so the persisted (and reloaded) definition matches what the server
            //     minted — the client cannot forge the id/tenant/version.
            JsonElement authored;
            try
            {
                var node = JsonNode.Parse(body.GetRawText())!.AsObject();
                node["key"] = key;
                node["tenant"] = tenant.Value;
                node["version"] = version;
                node["status"] = WorkflowDefinitionStatus.Published.ToString();
                using var doc = JsonDocument.Parse(node.ToJsonString());
                authored = doc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = $"malformed workflow definition: {ex.Message}" });
            }

            // Register maps this exact wire inside the lifecycle, admits that model, then persists this wire.
            try
            {
                await store.RegisterAndPublishAsync(authored, decision, ct).ConfigureAwait(false);
            }
            catch (AuthorizationDeniedException denial)
            {
                // The persist stage re-checks that the carried authority still describes THIS act; a
                // mismatch is a refusal, and it is rendered exactly like the one above.
                return await RequestAuthorization.RefusedAsync(http, denial, ct).ConfigureAwait(false);
            }
            catch (GateReferenceShapeException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message, code = ex.Code, field = ex.Field });
            }
            catch (WorkflowGateLaneException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message, code = ex.Code, field = ex.Field });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (WorkflowAdmissionException ex)
            {
                // Surface the stable, locale-independent code (client localizes off the CODE) — the fleet's
                // "validation errors are codes, not English literals" rule.
                var first = ex.Result.Violations.Count > 0 ? ex.Result.Violations[0] : default;
                return Results.UnprocessableEntity(new { error = ex.Message, code = first.Code, locator = first.Locator });
            }
            catch (RoleGateAdmissionException ex)
            {
                return Results.UnprocessableEntity(new
                {
                    error = ex.Message,
                    code = ex.Code,
                    definition = ex.Finding.DefinitionId,
                    gate = ex.Finding.Gate,
                    role = ex.Finding.Subject,
                    rule = ex.Finding.Rule,
                });
            }
            catch (DefinitionProvenanceException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message, code = ex.Code });
            }
            catch (WorkflowDefinitionConflictException)
            {
                return Results.Conflict(new { error = $"A revision of '{key}' at version {version} already exists." });
            }

            return Results.Ok(new SaveWorkflowDefinitionResponse(key, version));
        });
    }

    /// <summary>Bumps the patch of a "{major}.{minor}.{patch}" version (non-numeric parts read as 0).</summary>
    private static string BumpPatch(string version)
    {
        var parts = version.Split('.');
        int At(int i) => parts.Length > i && int.TryParse(parts[i], out var n) ? n : 0;
        return $"{At(0)}.{At(1)}.{At(2) + 1}";
    }
}

// ── Wire → lean-model mapping ─────────────────────────────────────────────────
//
// The wire→model parser (WorkflowDefinitionWireMapper.ToModel, called at :137 above) now lives in
// blocks-workflow (Harborline.Api.Blocks.Workflow.Durable) so ONE canonical parse feeds register-time
// admission (here) AND load-time re-validation (WorkflowDefinitionLoadValidator, ADR 0135 A1 R-1 /
// ADR 0143 R1-E) with no drift. See packages/blocks-workflow/src/durable/WorkflowDefinitionWireMapper.cs.

// ── Wire shapes (response side) ────────────────────────────────────────────────

/// <summary>The 200 response after a successful save — the saved key + the minted version.</summary>
public sealed record SaveWorkflowDefinitionResponse(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// A list entry for the tenant's definitions (GET list) — key, version, display name, and the
/// authored `updatedAt` (front-door manager last-modified column, builder-ux-rev2 §E). `UpdatedAt`
/// is read from <see cref="WorkflowDefinitionRecord.Authored"/> (the same JSON the Harborline App PUT —
/// `toWorkflowDefinition` always stamps it) rather than tracked separately by the store.
/// </summary>
public sealed record WorkflowDefinitionSummaryDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt)
{
    public static WorkflowDefinitionSummaryDto From(WorkflowDefinitionRecord record)
    {
        var name = record.Key;
        if (record.Authored.TryGetProperty("title", out var title)
            && title.TryGetProperty("values", out var values)
            && values.TryGetProperty("en", out var en)
            && en.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(en.GetString()))
        {
            name = en.GetString()!;
        }
        string? updatedAt = null;
        if (record.Authored.TryGetProperty("updatedAt", out var updatedAtEl)
            && updatedAtEl.ValueKind == JsonValueKind.String)
        {
            updatedAt = updatedAtEl.GetString();
        }
        return new WorkflowDefinitionSummaryDto(record.Key, record.Version, name, updatedAt);
    }
}
