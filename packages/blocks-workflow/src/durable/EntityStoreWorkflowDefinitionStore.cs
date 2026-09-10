using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// Durable <see cref="IWorkflowDefinitionStore"/> backed by the asset
/// <see cref="IEntityStore"/> — the process analog of
/// <c>EntityStoreFormDefinitionStore</c> (ADR 0055 §"Entity Store"). Each definition
/// revision is one versioned entity under a reserved schema; "current published" is a
/// JSON-containment query over <c>body_json</c> (the partial GIN content-index,
/// earlier repository ticket #221).
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-agnostic by design.</b> Composes the <see cref="IEntityStore"/> abstraction,
/// not a concrete backend, so durability is injected at the composition root: backed by
/// <see cref="InMemoryEntityStore"/> it is a faithful double for tests; backed by a durable
/// entity store it is the production store — the same layering the forms store uses.
/// </para>
/// <para>
/// <b>Fail-closed admission (the security keystone of W-7).</b> <c>RegisterAsync</c>
/// runs the shipped <see cref="IWorkflowAdmissionValidator"/> (registry-derived
/// classification, ADR 0143 / #1638) BEFORE the store write. An inadmissible definition
/// throws <see cref="WorkflowAdmissionException"/> and NOTHING is persisted — the process
/// analog of the forms store's <c>ValidateOverlayOrThrow</c> at register. Because the gate
/// lives in the store (not only the HTTP route), every persist path is fail-closed.
/// </para>
/// <para>
/// <b>Envelope.</b> The stored body is a query-stable envelope (<c>kind</c>, <c>tenant</c>,
/// <c>key</c>, <c>version</c>, <c>status</c>) with the full authored definition under
/// <c>authored</c>. The explicit query keys mean a refactor of the definition shape cannot
/// silently break the containment query. The <see cref="WorkflowDefinition"/> admission
/// model is used only to derive the keys + run admission; it is not stored (it carries no
/// display chrome — persisting <c>authored</c> is what round-trips the builder intact).
/// </para>
/// <para>
/// <b>Identity.</b> Each <c>(tenant, key, version)</c> maps to a deterministic
/// <see cref="EntityId"/> via <see cref="CreateOptions.ExplicitLocalPart"/>, so reads by
/// exact version and lifecycle transitions are O(1) entity-store lookups.
/// </para>
/// </remarks>
public sealed class EntityStoreWorkflowDefinitionStore
    : EntityStoreDefinitionLifecycle<WorkflowDefinitionRecord>, IWorkflowDefinitionStore,
      IWorkflowDefinitionExecutionStore
{
    /// <summary>Reserved schema id under which every workflow-definition envelope is stored.</summary>
    internal static readonly SchemaId DefinitionSchema = new("sunfish.workflow-definition");

    private const string EnvelopeKind = "workflow-definition";
    private const string EntityScheme = "workflowdef";
    private const string EntityAuthority = "workflows";

    /// <summary>The actor recorded on each mint/transition. The definition carries no owner
    /// (the lean keystone models none); the tenant boundary — not this actor — is the isolation.</summary>
    internal static readonly ActorId DefinitionAuthor = new("node:workflow-definition-author");

    private readonly IWorkflowAdmissionValidator _admission;

    /// <summary>Constructs the store over an entity store, the admission validator, and a clock.</summary>
    public EntityStoreWorkflowDefinitionStore(
        IEntityStore store,
        IEntityMutationStore mutations,
        IWorkflowAdmissionValidator admission,
        EntityBodyAdmission bodies,
        TimeProvider time)
        : base(store, mutations, bodies, time, DefinitionSchema, EnvelopeKind, "key", EntityScheme, EntityAuthority)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(time);
        _admission = admission;
    }

    public EntityStoreWorkflowDefinitionStore(
        IEntityStore store, IWorkflowAdmissionValidator admission, EntityBodyAdmission bodies, TimeProvider time)
        : this(store, RequireMutations(store), admission, bodies, time)
    {
    }

    private static IEntityMutationStore RequireMutations(IEntityStore store) =>
        store as IEntityMutationStore
        ?? throw new ArgumentException("The entity reader must share one instance with its unregistered mutation face.", nameof(store));

    /// <summary>
    /// LOAD-FOR-EXECUTION path (ADR 0135 A1 R-1 / ADR 0143 R1-E). Loads the highest-version Published
    /// revision AND re-runs admission on its persisted <c>authored</c> JSON — fail-closed: a definition that
    /// was admitted under an older capability registry but is NO LONGER admissible (e.g. a capability was
    /// reclassified to CP, or an edit routed a CP edge around the human-task) throws
    /// <see cref="WorkflowAdmissionException"/> and MUST NOT be handed out for execution. Returns
    /// <see langword="null"/> when nothing is published. Distinct from
    /// <see cref="IDefinitionLifecycleStore{TDefinition}.GetCurrentPublishedAsync"/>
    /// (the lenient builder-reload read) — an executor / instantiation / D7-re-pin path calls THIS.
    /// </summary>
    public async ValueTask<WorkflowDefinitionRecord?> GetAdmittedCurrentPublishedAsync(
        DefinitionAddress address, CancellationToken ct = default)
    {
        var record = await GetCurrentPublishedAsync(address, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        // Re-validate the PERSISTED authored JSON with the SAME canonical parse + admission validator the
        // register-time path used (no drift). Throws WorkflowAdmissionException if now-inadmissible.
        _ = new WorkflowDefinitionLoadValidator(_admission)
            .ReadAdmissibleOrThrow(record.Authored, record.Tenant, record.Key, record.Version);
        return record;
    }

    /// <summary>
    /// LOAD-FOR-EXECUTION path for an exact <c>(tenant, key, version)</c> revision (ADR 0135 A1 R-1) — the
    /// D7-pinned version an instance resolves replay against. Loads the revision AND re-runs admission
    /// (fail-closed, as <see cref="GetAdmittedCurrentPublishedAsync"/>). Throws
    /// <see cref="WorkflowDefinitionNotFoundException"/> if absent, <see cref="WorkflowAdmissionException"/>
    /// if now-inadmissible.
    /// </summary>
    public async ValueTask<WorkflowDefinitionRecord> GetAdmittedAsync(
        DefinitionCoordinates coordinates, CancellationToken ct = default)
    {
        var tenant = coordinates.Address.Tenant.Value;
        var key = coordinates.Address.Identity.Value;
        var version = coordinates.Version.ToString();
        var record = await GetAsync(coordinates, ct).ConfigureAwait(false);
        if (record.Status != WorkflowDefinitionStatus.Published)
        {
            throw new WorkflowDefinitionNotFoundException(key, version, tenant);
        }
        _ = new WorkflowDefinitionLoadValidator(_admission)
            .ReadAdmissibleOrThrow(record.Authored, record.Tenant, record.Key, record.Version);
        return record;
    }

    internal static bool IsSystemPackProjection(JsonElement authored)
    {
        if (!authored.TryGetProperty("provenance", out var provenance)
            || !string.Equals(provenance.GetString(), "Pack", StringComparison.Ordinal))
        {
            return false;
        }

        return authored.TryGetProperty("owner", out var owner)
            && owner.TryGetProperty("scheme", out var scheme)
            && owner.TryGetProperty("value", out var value)
            && string.Equals(scheme.GetString(), "system", StringComparison.Ordinal)
            && string.Equals(value.GetString(), "__sunfish", StringComparison.Ordinal);
    }

    internal static JsonDocument SerializeEnvelope(
        DefinitionEnvelope<WorkflowDefinitionKey, WorkflowDefinitionVersion, TenantId, WorkflowDefinitionProvenance> definitionEnvelope,
        WorkflowDefinitionStatus status,
        JsonElement authored,
        PackProjectionSource? packSource = null,
        DateTimeOffset updatedAt = default)
    {
        var envelope = new JsonObject
        {
            ["kind"] = EnvelopeKind,
            ["tenant"] = definitionEnvelope.Tenant.Value,
            ["key"] = definitionEnvelope.Identity.Value,
            ["version"] = definitionEnvelope.Version.ToString(),
            ["cascadeLayer"] = definitionEnvelope.CascadeLayer.ToString(),
            ["requires"] = JsonSerializer.SerializeToNode(definitionEnvelope.Requires),
            ["status"] = status.ToString(),
            ["authored"] = JsonNode.Parse(authored.GetRawText()),
            ["packSource"] = packSource is null ? null : JsonSerializer.SerializeToNode(packSource),
            ["updatedAt"] = updatedAt,
        };
        return JsonDocument.Parse(envelope.ToJsonString());
    }

    internal static WorkflowDefinitionRecord ToRecord(JsonDocument body)
    {
        var root = body.RootElement;
        var tenant = root.GetProperty("tenant").GetString()
            ?? throw new JsonException("workflow-definition envelope had a null 'tenant'.");
        var key = root.GetProperty("key").GetString()
            ?? throw new JsonException("workflow-definition envelope had a null 'key'.");
        var version = root.GetProperty("version").GetString()
            ?? throw new JsonException("workflow-definition envelope had a null 'version'.");
        var status = Enum.Parse<WorkflowDefinitionStatus>(
            root.GetProperty("status").GetString()
            ?? throw new JsonException("workflow-definition envelope had a null 'status'."));
        var cascadeLayer = root.TryGetProperty("cascadeLayer", out var cascadeLayerElement)
            ? Enum.Parse<CascadeLayer>(cascadeLayerElement.GetString()
                ?? throw new JsonException("workflow-definition envelope had a null 'cascadeLayer'."))
            : CascadeLayer.Tenant;
        var requires = root.TryGetProperty("requires", out var requiresElement)
            ? requiresElement.Deserialize<IReadOnlyList<DefinitionRequirement>>()
                ?? throw new JsonException("workflow-definition envelope had a null 'requires'.")
            : Array.Empty<DefinitionRequirement>();
        // Clone so the returned element outlives the entity's backing JsonDocument.
        var authored = root.GetProperty("authored").Clone();
        var definitionEnvelope = new DefinitionEnvelope<
            WorkflowDefinitionKey,
            WorkflowDefinitionVersion,
            TenantId,
            WorkflowDefinitionProvenance>(
                new WorkflowDefinitionKey(key),
                WorkflowDefinitionVersion.Parse(version),
                new TenantId(tenant),
                cascadeLayer,
                WorkflowDefinitionProvenance.Unspecified,
                requires);
        var packSource = root.TryGetProperty("packSource", out var packSourceElement)
            && packSourceElement.ValueKind is not JsonValueKind.Null
            ? packSourceElement.Deserialize<PackProjectionSource>()
            : null;
        var updatedAt = root.TryGetProperty("updatedAt", out var updatedAtElement)
            ? updatedAtElement.GetDateTimeOffset()
            : default;
        return new WorkflowDefinitionRecord(definitionEnvelope, status, authored)
        {
            PackSource = packSource,
            UpdatedAt = updatedAt,
        };
    }

    /// <inheritdoc />
    protected override DefinitionCoordinates CoordinatesOf(WorkflowDefinitionRecord definition)
        => new(definition.Envelope.Tenant, definition.Key, definition.Version);

    /// <inheritdoc />
    protected override DefinitionLifecycleStatus StatusOf(WorkflowDefinitionRecord definition)
        => (DefinitionLifecycleStatus)definition.Status;

    /// <inheritdoc />
    protected override WorkflowDefinitionRecord WithStatus(
        WorkflowDefinitionRecord definition,
        DefinitionLifecycleStatus status,
        DateTimeOffset transitionedAt)
        => definition with
        {
            Status = (WorkflowDefinitionStatus)status,
            UpdatedAt = transitionedAt,
        };

    /// <inheritdoc />
    protected override JsonDocument Serialize(WorkflowDefinitionRecord definition)
        => SerializeEnvelope(
            definition.Envelope,
            definition.Status,
            definition.Authored,
            definition.PackSource,
            definition.UpdatedAt);

    /// <inheritdoc />
    protected override WorkflowDefinitionRecord Deserialize(JsonDocument body) => ToRecord(body);

    /// <inheritdoc />
    protected override ActorId TransitionActor(WorkflowDefinitionRecord definition) => DefinitionAuthor;

    /// <inheritdoc />
    protected override Exception CreateNotFoundException(DefinitionCoordinates coordinates)
        => new WorkflowDefinitionNotFoundException(
            coordinates.Address.Identity.Value,
            coordinates.Version.ToString(),
            coordinates.Address.Tenant.Value);

    /// <inheritdoc />
    protected override Exception CreateInvalidTransitionException(
        WorkflowDefinitionRecord definition,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom)
        => new InvalidOperationException(
            $"WorkflowDefinition '{definition.Key}' v{definition.Version} cannot transition "
            + $"from {definition.Status} to {target}.");

    /// <inheritdoc />
    protected override ValueTask ValidatePackRestoreAsync(
        WorkflowDefinitionRecord definition,
        CancellationToken cancellationToken)
    {
        if (!IsSystemPackProjection(definition.Authored))
        {
            throw new InvalidOperationException(
                $"Only a System-owned, Pack-provenance workflow projection can be restored; "
                + $"'{definition.Key}' v{definition.Version} is not one.");
        }
        if (definition.Status is not (WorkflowDefinitionStatus.Withdrawn or WorkflowDefinitionStatus.Published))
        {
            throw new InvalidOperationException(
                $"WorkflowDefinition '{definition.Key}' v{definition.Version} cannot be restored from {definition.Status}.");
        }

        _ = new WorkflowDefinitionLoadValidator(_admission)
            .ReadAdmissibleOrThrow(
                definition.Authored,
                definition.Tenant,
                definition.Key,
                definition.Version);
        return ValueTask.CompletedTask;
    }
}
