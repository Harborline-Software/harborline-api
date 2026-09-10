using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

namespace Harborline.Api.LocalNodeHost.Data.Compose;

/// <summary>
/// The Pack Composer's outbound ceremony (B-2a) — the server-side engine behind the guarded export.
/// Implements the binding council fold (cerebrum 2026-07-06):
/// <list type="bullet">
/// <item><b>Q3 SNAPSHOT-AT-COMPOSE, read-live REFUSED</b>: <see cref="ComposeAsync"/> reads the selected
/// authored artifacts from their live stores ONCE, canonicalizes each into a frozen leaf, and hashes the
/// whole set. Export never re-reads the live stores.</item>
/// <item><b>S-1 affirmation bound to the snapshot hash, SERVER-SIDE</b>: <see cref="Affirm"/> records the
/// human PII-review affirmation ONLY against the exact current snapshot hash (a UI cannot fake a checkbox
/// against different bytes); <see cref="ExportAsync"/> re-hashes the frozen draft and refuses to sign unless
/// the affirmation still matches ("you sign what you inspected").</item>
/// <item><b>Q1 DCP export gate</b>: export runs through <see cref="IPackExporter"/>, whose fail-closed DCP
/// gate (ADR 0145) blocks a missing DCP and a non-counsel-cleared RegulatoryClass — the ceremony surfaces,
/// never bypasses, those refusals.</item>
/// </list>
/// v1 packages asset types and published form definitions. Rules and help remain deferred.
/// </summary>
public sealed class ComposeCeremony
{
    private readonly IEntityTypeRegistry _types;
    private readonly PackContentCanonicalizer _canonicalizer;
    private readonly PackDcpCanonicalizer _dcpCanonicalizer;
    private readonly IDraftCompositionStore _store;
    private readonly IFormDefinitionStore? _forms;
    private readonly ISchemaRegistry? _schemas;
    private readonly TimeProvider _clock;

    /// <summary>Constructs the ceremony over the authored-artifact registry + the pack canonicalizers.</summary>
    public ComposeCeremony(
        IEntityTypeRegistry types,
        PackContentCanonicalizer canonicalizer,
        PackDcpCanonicalizer dcpCanonicalizer,
        IDraftCompositionStore store,
        TimeProvider? clock = null,
        IFormDefinitionStore? forms = null,
        ISchemaRegistry? schemas = null)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
        _dcpCanonicalizer = dcpCanonicalizer ?? throw new ArgumentNullException(nameof(dcpCanonicalizer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _forms = forms;
        _schemas = schemas;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // ── (1) Compose — snapshot-at-compose (Q3) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshots the selected authored artifacts into a frozen draft composition, canonicalizing each into a
    /// content-addressed leaf and hashing the whole set. Reads the live stores ONCE (read-live at export is
    /// refused, Q3). Re-composing an existing <paramref name="request"/>.<see cref="ComposeRequest.ComposeId"/>
    /// REPLACES the draft with a new frozen set + hash, invalidating any prior affirmation.
    /// </summary>
    public async Task<ComposeOutcome> ComposeAsync(ComposeRequest request, TenantId tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return ComposeOutcome.Invalid(Err("pack.compose.key.missing", null, "a pack key is required."));
        }
        var formIds = request.FormIds ?? Array.Empty<string>();
        if (request.TypeIds.Count == 0 && formIds.Count == 0)
        {
            return ComposeOutcome.Invalid(Err("pack.compose.empty", null,
                "a pack must carry at least one content item."));
        }

        // Snapshot each selected asset type from the live registry → a frozen, content-addressed leaf.
        var leaves = new List<ComposedLeaf>(request.TypeIds.Count + formIds.Count);
        var warnings = new List<ComposeWarning>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawId in request.TypeIds)
        {
            if (string.IsNullOrWhiteSpace(rawId)) continue;
            var typeId = new EntityTypeId(rawId.Trim());

            // Effective descriptor: a tenant row overrides the shared seed (mirrors the Type Manager catalog).
            var descriptor =
                (await _types.GetTypeAsync(tenant, typeId, ct).ConfigureAwait(false))?.Descriptor
                ?? _types.GetSeed(typeId)?.Descriptor;
            if (descriptor is null)
            {
                return ComposeOutcome.Invalid(Err("pack.compose.type.not_found", typeId.Value,
                    $"asset type '{typeId.Value}' is not known to this instance."));
            }
            if (!seenKeys.Add(typeId.Value))
            {
                return ComposeOutcome.Invalid(Err("pack.compose.type.duplicate", typeId.Value,
                    $"asset type '{typeId.Value}' was selected more than once."));
            }

            // The property-form binding travels ONLY when the bound form is a leaf of THIS pack (ticket 357):
            // the content key is a pack-local key, so a binding into another pack (or into a form the author
            // did not select) cannot resolve on the target node and is dropped here — never silently, the
            // existing lossy-binding warning names it.
            // The exported binding is a KEY only, so it re-pins to whatever version of that form THIS
            // composition snapshots (below, at the form's current published version) — it does not preserve
            // the version the source descriptor bound. Key-only is the self-consistent choice: the
            // alternative pins a version this pack may not carry.
            var propertyForm = descriptor.PropertyFormBinding;
            var bindingTravels = propertyForm is not null
                                 && formIds.Contains(propertyForm.Definition.Value, StringComparer.Ordinal);
            var composedDescriptor = bindingTravels ? descriptor : descriptor with { PropertyFormBinding = null };

            var content = PackAssetTypeContent.ToContent(typeId, composedDescriptor);
            var item = _canonicalizer.Canonicalize(new PackContentSource(
                typeId.Value,
                PackContentKind.AssetTypeDefinition,
                PackAssetTypeContent.ContentVersionFor(composedDescriptor, request.Version),
                content));
            leaves.Add(new ComposedLeaf(
                item.Key, item.Kind, item.Version, content, item.ContentAddress.Value));

            // Compose-time detection (#141): the projection (ToContent) cannot carry every form binding a
            // type SETS, so such a type would ship WITHOUT it. Surface a structured, localizable warning
            // rather than dropping it silently — the author decides whether to include the type anyway.
            var droppedFormBindings = new List<string>(
                PackAssetTypeContent.DroppedFormBindingFields(composedDescriptor));
            if (propertyForm is not null && !bindingTravels)
            {
                droppedFormBindings.Insert(0, "propertyFormBinding");
            }
            if (droppedFormBindings.Count > 0)
            {
                warnings.Add(new ComposeWarning(
                    ComposeWarningCodes.ProjectionLossyFormBinding,
                    typeId.Value,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["fields"] = string.Join(",", droppedFormBindings),
                    }));
            }
        }

        if (formIds.Count > 0 && (_forms is null || _schemas is null))
        {
            return ComposeOutcome.Invalid(Err(
                "pack.compose.form.store_unavailable", null, "the form-definition store is unavailable."));
        }

        // Snapshot each selected form at the current PUBLISHED revision. The form store owns the overlay;
        // the schema registry owns its content-addressed schema. PackFormDefinitionContent recombines them
        // into the pinned authoring wire contract the install projector consumes.
        foreach (var rawId in formIds)
        {
            if (string.IsNullOrWhiteSpace(rawId)) continue;
            var formId = new FormDefinitionId(rawId.Trim());
            if (!seenKeys.Add(formId.Value))
            {
                return ComposeOutcome.Invalid(Err("pack.compose.content.duplicate", formId.Value,
                    $"content key '{formId.Value}' was selected more than once."));
            }

            var definition = await _forms!
                .GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId.Value), ct)
                .ConfigureAwait(false);
            if (definition is null)
            {
                return ComposeOutcome.Invalid(Err("pack.compose.form.not_found", formId.Value,
                    $"published form '{formId.Value}' is not known to this tenant."));
            }

            var schema = await _schemas!.GetAsync(definition.SchemaRef, ct).ConfigureAwait(false);
            if (schema is null)
            {
                return ComposeOutcome.Invalid(Err("pack.compose.form.schema_not_found", formId.Value,
                    $"published form '{formId.Value}' references an unavailable schema."));
            }

            JsonNode content;
            try
            {
                content = PackFormDefinitionContent.ToContent(definition, schema);
            }
            catch (JsonException ex)
            {
                return ComposeOutcome.Invalid(Err("pack.compose.form.projection_failed", formId.Value, ex.Message));
            }

            var item = _canonicalizer.Canonicalize(new PackContentSource(
                formId.Value, PackContentKind.FormDefinition, definition.Version.ToString(), content));
            leaves.Add(new ComposedLeaf(item.Key, item.Kind, item.Version, content, item.ContentAddress.Value));
        }

        // A request containing only blank ids must not bypass the non-empty composition invariant.
        if (leaves.Count == 0)
        {
            return ComposeOutcome.Invalid(Err("pack.compose.empty", null,
                "a pack must carry at least one content item."));
        }

        var dcp = request.Dcp; // route grandfathers a missing DCP to `general` before we get here.
        var snapshotHash = ComputeSnapshotHash(
            request.Key, request.Version, request.Name, request.Description, request.ScopeTier,
            leaves, dcp, request.Dependencies, request.CapabilityRequirements);

        var composeId = string.IsNullOrWhiteSpace(request.ComposeId) ? Guid.NewGuid().ToString("N") : request.ComposeId;
        var draft = new DraftComposition
        {
            ComposeId = composeId,
            Tenant = tenant,
            Key = request.Key,
            Version = request.Version,
            Name = string.IsNullOrWhiteSpace(request.Name) ? request.Key : request.Name,
            Description = request.Description ?? string.Empty,
            ScopeTier = request.ScopeTier,
            Leaves = leaves,
            Dcp = dcp,
            Dependencies = request.Dependencies ?? Array.Empty<PackDependencyRef>(),
            CapabilityRequirements = request.CapabilityRequirements ?? Array.Empty<string>(),
            SnapshotHash = snapshotHash,
            Affirmation = null, // re-compose always clears the prior affirmation (content changed).
            Warnings = warnings,
        };
        _store.Save(draft);
        return ComposeOutcome.Ok(draft);
    }

    /// <summary>Re-reads a frozen draft (tenant-scoped) so a reloaded Harborline App can re-render the review.</summary>
    public DraftComposition? GetDraft(string composeId, TenantId tenant) => _store.Get(composeId, tenant);

    // ── (2) Affirm — bind the human PII review to the snapshot hash (S-1) ───────────────────────────────

    /// <summary>
    /// Records the human PII-review affirmation, bound to the EXACT current snapshot hash. Refuses
    /// (<see cref="ComposeStatus.SnapshotMismatch"/>) if <paramref name="affirmedHash"/> is not the draft's
    /// current <see cref="DraftComposition.SnapshotHash"/> — the affirmation cannot attach to bytes the human
    /// did not review (S-1). This is the server-side binding that replaces a UI checkbox.
    /// </summary>
    public ComposeOutcome Affirm(string composeId, TenantId tenant, string affirmedHash)
    {
        var draft = _store.Get(composeId, tenant);
        if (draft is null) return ComposeOutcome.NotFound();

        if (string.IsNullOrWhiteSpace(affirmedHash) || !StringEquals(affirmedHash, draft.SnapshotHash))
        {
            return ComposeOutcome.Mismatch(draft);
        }

        var affirmed = draft with { Affirmation = new ComposeAffirmation(draft.SnapshotHash, _clock.GetUtcNow()) };
        _store.Save(affirmed);
        return ComposeOutcome.Ok(affirmed);
    }

    // ── (3) Export — re-hash before signing, then the guarded exporter (S-1 / Q1) ───────────────────────

    /// <summary>
    /// The guarded outbound step. Refuses (fail-closed) unless a human affirmation is present AND still binds
    /// the CURRENT snapshot (re-hashed here — "you sign what you inspected", S-1). Then delegates to
    /// <see cref="IPackExporter"/>, whose DCP gate (ADR 0145) + completeness/PII validator can still refuse
    /// (a missing DCP, a non-counsel-cleared class, instance-data content) — the ceremony surfaces those
    /// findings, never an install-anyway path (S-13). On success the draft is consumed (removed).
    /// </summary>
    public async Task<ComposeExportOutcome> ExportAsync(
        string composeId, TenantId tenant, IPackExporter exporter, IOperationSigner signer, long epoch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(signer);

        var draft = _store.Get(composeId, tenant);
        if (draft is null) return ComposeExportOutcome.NotFound();

        // Fail-closed: the human PII review (S-12 / FU-1) must have run.
        if (draft.Affirmation is null)
        {
            return ComposeExportOutcome.AffirmationRequired();
        }

        // Re-hash the frozen draft — "you sign what you inspected" (S-1). A stale affirmation (the draft was
        // re-composed after the human affirmed) no longer matches the current hash ⇒ refuse.
        var currentHash = ComputeSnapshotHash(
            draft.Key, draft.Version, draft.Name, draft.Description, draft.ScopeTier,
            draft.Leaves, draft.Dcp, draft.Dependencies, draft.CapabilityRequirements);
        if (!StringEquals(currentHash, draft.SnapshotHash) || !StringEquals(draft.Affirmation.AffirmedHash, currentHash))
        {
            return ComposeExportOutcome.Mismatch();
        }

        var contents = draft.Leaves
            .Select(l => new PackContentSource(l.Key, l.Kind, l.Version, l.Content))
            .ToList();

        var exportRequest = new PackExportRequest(
            Key: draft.Key,
            Version: draft.Version,
            Name: draft.Name,
            Description: draft.Description,
            ScopeTier: draft.ScopeTier,
            Contents: contents,
            Dependencies: draft.Dependencies,
            CapabilityRequirements: draft.CapabilityRequirements,
            Epoch: epoch,
            RenamedFrom: null,
            Dcp: draft.Dcp);

        var outcome = await exporter.ExportAsync(exportRequest, signer, ct).ConfigureAwait(false);
        if (!outcome.Succeeded || outcome.FileBytes is null)
        {
            // DCP gate / completeness refusal — surface the codes, never sign an invalid pack (S-13).
            return ComposeExportOutcome.ValidationFailed(outcome.Validation.Errors);
        }

        _store.Remove(composeId, tenant); // consumed — a signed file left the instance (S-5).
        return ComposeExportOutcome.Exported(outcome.FileBytes, $"{draft.Key}-{draft.Version}.pack");
    }

    // ── Snapshot hashing — the manifest digest the review + affirmation bind to (S-1) ───────────────────

    /// <summary>
    /// Deterministic content-address over the whole frozen draft: pack metadata + every leaf's
    /// content-address + the DCP's content-address, canonicalized by the ONE canonicalizer (S-14). Any
    /// change to the composed set (a leaf added/removed/changed, a DCP edit) changes this hash — which is
    /// exactly what makes a prior affirmation stale.
    /// </summary>
    private string ComputeSnapshotHash(
        string key, string version, string? name, string? description, PackScopeTier scopeTier,
        IReadOnlyList<ComposedLeaf> leaves, DomainComplianceProfile? dcp,
        IReadOnlyList<PackDependencyRef>? dependencies, IReadOnlyList<string>? capabilityRequirements)
    {
        var contents = new JsonArray();
        foreach (var l in leaves)
        {
            contents.Add((JsonNode)new JsonObject
            {
                ["key"] = l.Key,
                ["kind"] = l.Kind.ToString(),
                ["version"] = l.Version,
                ["cid"] = l.ContentAddress,
            });
        }

        var deps = new JsonArray();
        foreach (var d in dependencies ?? Array.Empty<PackDependencyRef>())
        {
            deps.Add((JsonNode)new JsonObject { ["key"] = d.Key, ["version"] = d.Version });
        }

        var caps = new JsonArray();
        foreach (var c in capabilityRequirements ?? Array.Empty<string>())
        {
            caps.Add((JsonNode)c);
        }

        var digest = new JsonObject
        {
            ["key"] = key,
            ["version"] = version,
            ["name"] = name ?? string.Empty,
            ["description"] = description ?? string.Empty,
            ["scopeTier"] = scopeTier.ToString(),
            ["contents"] = contents,
            ["dcpCid"] = dcp is { } profile ? _dcpCanonicalizer.Canonicalize(profile).ContentAddress.Value : null,
            ["dependencies"] = deps,
            ["capabilityRequirements"] = caps,
        };

        return Cid.FromBytes(CanonicalJson.Serialize<JsonNode>(digest)).Value;
    }

    private static bool StringEquals(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    private static IReadOnlyList<PackValidationError> Err(string code, string? target, string message)
        => new[] { new PackValidationError(code, target, message) };
}
