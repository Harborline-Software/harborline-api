using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Durable <see cref="IFormDefinitionStore"/> backed by the asset
/// <see cref="IEntityStore"/> (ADR 0055 §"Entity Store"). Each form definition is
/// persisted as one versioned entity under a reserved schema; lookups by
/// "current published" are served by a JSON-containment query that lights up the
/// partial GIN content-index over <c>entities.body_json</c> (earlier repository ticket #221).
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-agnostic by design.</b> This store composes the <see cref="IEntityStore"/>
/// abstraction, not a concrete backend, so durability is injected at the composition
/// root: a host registers an <see cref="IEntityStore"/> (a durable adapter in
/// production, <c>AddHarborlineAssetsInMemory()</c> in tests) and then
/// <c>AddEntityStoreFormDefinitionStore()</c>. Backed by Postgres it is the durable
/// production store; backed by <see cref="InMemoryEntityStore"/> it is a faithful
/// double for tests. The directive named this "PostgresFormDefinitionStore"; composing
/// the abstraction instead of the Postgres package keeps the layering correct (the
/// generic storage backend must not depend on the forms keystone) and gives durability
/// for free wherever an <see cref="IEntityStore"/> is registered.
/// </para>
/// <para>
/// <b>Envelope.</b> The stored body is a query-stable envelope decoupled from the
/// <see cref="FormDefinition"/> record shape — the query keys (<c>kind</c>,
/// <c>tenant</c>, <c>formId</c>, <c>version</c>, <c>status</c>) are explicit so that a
/// future refactor of <see cref="FormDefinition"/> cannot silently break the
/// containment query. The full serialized definition lives under <c>definition</c>.
/// </para>
/// <para>
/// <b>Identity.</b> Each (tenant, formId, version) tuple maps to a deterministic
/// <see cref="EntityId"/> via <see cref="CreateOptions.ExplicitLocalPart"/>, so reads
/// by exact version and lifecycle transitions are O(1) entity-store lookups.
/// </para>
/// </remarks>
public sealed class EntityStoreFormDefinitionStore : EntityStoreDefinitionLifecycle<FormDefinition>,
    IFormDefinitionStore
{
    /// <summary>Reserved schema id under which every form-definition envelope is stored.</summary>
    internal static readonly SchemaId DefinitionSchema = new("sunfish.form-definition");

    private const string EnvelopeKind = "form-definition";
    private const string EntityScheme = "formdef";
    private const string EntityAuthority = "forms";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Constructs the store over the supplied entity store and clock.</summary>
    /// <param name="store">The durable (or in-memory) entity store backing persistence.</param>
    /// <param name="mutations">The same store's unregistered mutation face.</param>
    /// <param name="time">Clock for lifecycle-transition timestamps.</param>
    public EntityStoreFormDefinitionStore(
        IEntityStore store,
        IEntityMutationStore mutations,
        TimeProvider time)
        : base(store, mutations, time, DefinitionSchema, EnvelopeKind, "formId", EntityScheme, EntityAuthority)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(time);
    }

    /// <summary>Constructs over a combined reader/mutation implementation without exposing it through DI.</summary>
    public EntityStoreFormDefinitionStore(IEntityStore store, TimeProvider time)
        : this(store, RequireMutations(store), time)
    {
    }

    private static IEntityMutationStore RequireMutations(IEntityStore store) =>
        store as IEntityMutationStore
        ?? throw new ArgumentException("The entity reader must share one instance with its unregistered mutation face.", nameof(store));

    /// <inheritdoc />
    protected override JsonDocument Serialize(FormDefinition definition) => SerializeDefinition(definition);

    internal static JsonDocument SerializeDefinition(FormDefinition definition)
    {
        var envelope = new JsonObject
        {
            ["kind"] = EnvelopeKind,
            ["tenant"] = definition.Tenant.Value,
            ["formId"] = definition.Id.Value,
            ["version"] = definition.Version.ToString(),
            ["status"] = definition.Status.ToString(),
            ["definition"] = JsonSerializer.SerializeToNode(definition, SerializerOptions),
        };
        return JsonDocument.Parse(envelope.ToJsonString());
    }

    /// <inheritdoc />
    protected override FormDefinition Deserialize(JsonDocument body) => DeserializeDefinition(body);

    internal static FormDefinition DeserializeDefinition(JsonDocument body)
    {
        var definitionElement = body.RootElement.GetProperty("definition");
        var definition = definitionElement.Deserialize<FormDefinition>(SerializerOptions)
            ?? throw new JsonException("form-definition envelope had a null 'definition' member.");
        return FormDefinitionFreezer.Freeze(definition);
    }

    internal static ActorId OwnerActor(IdentityRef owner) => new(owner.ToString());

    private static (TenantId Tenant, FormDefinitionId Id, SemanticVersion Version) ToFormCoordinates(
        DefinitionCoordinates coordinates)
        => (
            coordinates.Address.Tenant,
            new FormDefinitionId(coordinates.Address.Identity.Value),
            new SemanticVersion(coordinates.Version.Major, coordinates.Version.Minor, coordinates.Version.Patch));

    /// <inheritdoc />
    protected override DefinitionCoordinates CoordinatesOf(FormDefinition definition)
        => new(definition.Tenant, definition.Id.Value, definition.Version.ToString());

    /// <inheritdoc />
    protected override DefinitionLifecycleStatus StatusOf(FormDefinition definition)
        => (DefinitionLifecycleStatus)definition.Status;

    /// <inheritdoc />
    protected override FormDefinition WithStatus(
        FormDefinition definition,
        DefinitionLifecycleStatus status,
        DateTimeOffset transitionedAt)
        => definition with
        {
            Status = (FormDefinitionStatus)status,
            UpdatedAt = transitionedAt,
        };

    /// <inheritdoc />
    protected override ActorId TransitionActor(FormDefinition definition) => OwnerActor(definition.Owner);

    /// <inheritdoc />
    protected override Exception CreateNotFoundException(DefinitionCoordinates coordinates)
    {
        var (tenant, id, version) = ToFormCoordinates(coordinates);
        return new FormDefinitionNotFoundException(id, version, tenant);
    }

    /// <inheritdoc />
    protected override Exception CreateInvalidTransitionException(
        FormDefinition definition,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom)
        => new InvalidOperationException(
            $"FormDefinition '{definition.Id}' v{definition.Version} cannot transition from {definition.Status} "
            + $"to {target}; allowed source statuses are [{string.Join(", ", allowedFrom)}].");

    /// <inheritdoc />
    protected override ValueTask ValidatePackRestoreAsync(
        FormDefinition definition,
        CancellationToken cancellationToken)
    {
        if (definition.Owner != IdentityRef.System)
        {
            throw new InvalidOperationException(
                $"Only a System-owned form projection can be restored; '{definition.Id}' "
                + $"v{definition.Version} is owned by {definition.Owner}.");
        }

        return ValueTask.CompletedTask;
    }
}
