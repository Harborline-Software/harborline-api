using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Foundation.Definitions;

/// <summary>The lifecycle states understood by the shared definition-store implementation.</summary>
public enum DefinitionLifecycleStatus
{
    /// <summary>The revision is editable and not live.</summary>
    Draft = 0,

    /// <summary>The revision is live.</summary>
    Published = 1,

    /// <summary>The revision remains readable but has been superseded.</summary>
    Deprecated = 2,

    /// <summary>The revision is retained but unavailable for use.</summary>
    Withdrawn = 3,
}

/// <summary>
/// Implements the six storage-neutral lifecycle operations once over the entity store while domain
/// adapters supply body serialization, status projection, and named domain failures.
/// </summary>
/// <typeparam name="TDefinition">The domain definition revision.</typeparam>
public abstract class EntityStoreDefinitionLifecycle<TDefinition> : IDefinitionLifecycleStore<TDefinition>
    where TDefinition : class
{
    private readonly SchemaId _schema;
    private readonly string _kind;
    private readonly string _identityProperty;
    private readonly string _entityScheme;
    private readonly string _entityAuthority;
    private readonly TimeProvider _time;

    /// <summary>Constructs a lifecycle implementation over an entity-store partition.</summary>
    protected EntityStoreDefinitionLifecycle(
        IEntityStore store,
        IEntityMutationStore mutations,
        EntityBodyAdmission admission,
        TimeProvider time,
        SchemaId schema,
        string kind,
        string identityProperty,
        string entityScheme,
        string entityAuthority)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        Admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _schema = schema;
        _kind = kind;
        _identityProperty = identityProperty;
        _entityScheme = entityScheme;
        _entityAuthority = entityAuthority;
    }

    /// <summary>The entity store used by lifecycle and domain-specific registration operations.</summary>
    protected IEntityStore Store { get; }

    /// <summary>The unregistered mutation face held only by the admitted lifecycle.</summary>
    protected IEntityMutationStore Mutations { get; }

    /// <summary>The write pipeline's validation stage, and the only mint of a store token.</summary>
    protected EntityBodyAdmission Admission { get; }

    /// <summary>
    /// The named admission path for a definition ENVELOPE: a body this lifecycle serialized itself,
    /// under an envelope schema the record registry does not hold, so the record-body validator has
    /// nothing to say about it. Reaching this requires holding <see cref="Mutations"/> — the raw port
    /// that DI deliberately does not register — and every holder is listed in
    /// <c>RawMutationPortSymbolInventoryTests</c> (ticket 151 candidate C).
    /// </summary>
    protected ValidatedBody AdmitEnvelope(SchemaId schema, JsonDocument body, string provenance) =>
        Admission.AdmitOwnValidated(Mutations, schema, body, provenance);

    /// <inheritdoc />
    public async ValueTask<TDefinition> GetAsync(
        DefinitionCoordinates coordinates,
        CancellationToken cancellationToken = default)
    {
        var entity = await Store
            .GetAsync(EntityIdFor(coordinates), VersionSelector.Latest, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            throw CreateNotFoundException(coordinates);
        }

        var definition = Deserialize(entity.Body);
        if (CoordinatesOf(definition) != coordinates)
        {
            throw CreateNotFoundException(coordinates);
        }

        return definition;
    }

    /// <inheritdoc />
    public async ValueTask<TDefinition?> GetCurrentPublishedAsync(
        DefinitionAddress address,
        CancellationToken cancellationToken = default)
    {
        var query = new EntityQuery(
            Schema: _schema,
            Tenant: TenantSelection.Of(address.Tenant),
            BodyContains: PublishedOperand(address.Identity));

        TDefinition? best = null;
        await foreach (var entity in Store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
        {
            var candidate = Deserialize(entity.Body);
            var coordinates = CoordinatesOf(candidate);
            if (StatusOf(candidate) != DefinitionLifecycleStatus.Published
                || coordinates.Address != address)
            {
                continue;
            }

            if (best is null || coordinates.Version.CompareTo(CoordinatesOf(best).Version) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <inheritdoc />
    protected ValueTask<TDefinition> PublishAsync(
        DefinitionCoordinates coordinates,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            coordinates,
            DefinitionLifecycleStatus.Published,
            [DefinitionLifecycleStatus.Draft, DefinitionLifecycleStatus.Published],
            cancellationToken);

    /// <inheritdoc />
    protected ValueTask<TDefinition> WithdrawAsync(
        DefinitionCoordinates coordinates,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            coordinates,
            DefinitionLifecycleStatus.Withdrawn,
            [
                DefinitionLifecycleStatus.Draft,
                DefinitionLifecycleStatus.Published,
                DefinitionLifecycleStatus.Deprecated,
                DefinitionLifecycleStatus.Withdrawn,
            ],
            cancellationToken);

    /// <inheritdoc />
    protected async ValueTask<TDefinition> RestorePackProjectionAsync(
        DefinitionCoordinates coordinates,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(coordinates, cancellationToken).ConfigureAwait(false);
        await ValidatePackRestoreAsync(existing, cancellationToken).ConfigureAwait(false);
        return await TransitionAsync(
            existing,
            coordinates,
            DefinitionLifecycleStatus.Published,
            [DefinitionLifecycleStatus.Withdrawn, DefinitionLifecycleStatus.Published],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TDefinition> ListByTenantAsync(
        TenantId tenant,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = new EntityQuery(Schema: _schema, Tenant: TenantSelection.Of(tenant));
        var definitions = new List<TDefinition>();
        await foreach (var entity in Store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(Deserialize(entity.Body));
        }

        definitions.Sort((left, right) =>
        {
            var leftCoordinates = CoordinatesOf(left);
            var rightCoordinates = CoordinatesOf(right);
            var byIdentity = string.CompareOrdinal(
                leftCoordinates.Address.Identity.Value,
                rightCoordinates.Address.Identity.Value);
            return byIdentity != 0
                ? byIdentity
                : leftCoordinates.Version.CompareTo(rightCoordinates.Version);
        });

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return definition;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TDefinition> ListPublishedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = new EntityQuery(Schema: _schema, Tenant: TenantSelection.All);
        var definitions = new List<TDefinition>();
        await foreach (var entity in Store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
        {
            var definition = Deserialize(entity.Body);
            if (StatusOf(definition) == DefinitionLifecycleStatus.Published)
            {
                definitions.Add(definition);
            }
        }

        // Health/catalogue consumers inspect the effective active head for each address. Older
        // published revisions remain retained for history and become effective again when a newer
        // revision is withdrawn.
        definitions = definitions
            .GroupBy(item => CoordinatesOf(item).Address)
            .Select(group => group.MaxBy(item => CoordinatesOf(item).Version)!)
            .ToList();

        definitions.Sort((left, right) =>
        {
            var leftCoordinates = CoordinatesOf(left);
            var rightCoordinates = CoordinatesOf(right);
            var byTenant = string.CompareOrdinal(
                leftCoordinates.Address.Tenant.Value,
                rightCoordinates.Address.Tenant.Value);
            if (byTenant != 0) return byTenant;
            var byIdentity = string.CompareOrdinal(
                leftCoordinates.Address.Identity.Value,
                rightCoordinates.Address.Identity.Value);
            return byIdentity != 0
                ? byIdentity
                : leftCoordinates.Version.CompareTo(rightCoordinates.Version);
        });

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return definition;
        }
    }

    /// <summary>Transitions a domain-specific lifecycle operation through the shared persistence path.</summary>
    protected ValueTask<TDefinition> TransitionAsync(
        DefinitionCoordinates coordinates,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom,
        CancellationToken cancellationToken = default)
        => TransitionLoadedAsync(coordinates, target, allowedFrom, cancellationToken);

    /// <summary>Transitions using an admitted caller-owned instant rather than the store clock.</summary>
    protected ValueTask<TDefinition> TransitionAsync(
        DefinitionCoordinates coordinates,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom,
        DateTimeOffset transitionedAt,
        CancellationToken cancellationToken = default)
        => TransitionLoadedAsync(coordinates, target, allowedFrom, transitionedAt, cancellationToken);

    /// <summary>Restores a validated pack revision using its admitted lifecycle instant.</summary>
    protected async ValueTask<TDefinition> RestorePackProjectionAtAsync(
        DefinitionCoordinates coordinates,
        DateTimeOffset transitionedAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(coordinates, cancellationToken).ConfigureAwait(false);
        await ValidatePackRestoreAsync(existing, cancellationToken).ConfigureAwait(false);
        return await TransitionAsync(
            existing,
            coordinates,
            DefinitionLifecycleStatus.Published,
            [DefinitionLifecycleStatus.Withdrawn, DefinitionLifecycleStatus.Published],
            transitionedAt,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the deterministic entity id for registration and lifecycle operations.</summary>
    protected EntityId EntityIdFor(DefinitionCoordinates coordinates)
        => new(_entityScheme, _entityAuthority, LocalPartFor(coordinates));

    /// <summary>Returns the stable idempotency nonce for registration.</summary>
    protected static string NonceFor(DefinitionCoordinates coordinates)
        => $"{coordinates.Address.Tenant.Value}|{coordinates.Address.Identity.Value}|{coordinates.Version}";

    /// <summary>Projects a domain definition to common coordinates.</summary>
    protected abstract DefinitionCoordinates CoordinatesOf(TDefinition definition);

    /// <summary>Projects a domain lifecycle status to the shared status.</summary>
    protected abstract DefinitionLifecycleStatus StatusOf(TDefinition definition);

    /// <summary>Creates a transitioned immutable domain revision.</summary>
    protected abstract TDefinition WithStatus(
        TDefinition definition,
        DefinitionLifecycleStatus status,
        DateTimeOffset transitionedAt);

    /// <summary>Serializes the complete persisted domain envelope.</summary>
    protected abstract JsonDocument Serialize(TDefinition definition);

    /// <summary>Deserializes the complete persisted domain envelope.</summary>
    protected abstract TDefinition Deserialize(JsonDocument body);

    /// <summary>Returns the actor recorded for a lifecycle transition.</summary>
    protected abstract ActorId TransitionActor(TDefinition definition);

    /// <summary>Creates the domain's opaque not-found failure.</summary>
    protected abstract Exception CreateNotFoundException(DefinitionCoordinates coordinates);

    /// <summary>Creates the domain's invalid-transition failure.</summary>
    protected abstract Exception CreateInvalidTransitionException(
        TDefinition definition,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom);

    /// <summary>Validates domain-specific pack provenance before a reverse projection.</summary>
    protected abstract ValueTask ValidatePackRestoreAsync(
        TDefinition definition,
        CancellationToken cancellationToken);

    private async ValueTask<TDefinition> TransitionLoadedAsync(
        DefinitionCoordinates coordinates,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(coordinates, cancellationToken).ConfigureAwait(false);
        return await TransitionAsync(existing, coordinates, target, allowedFrom, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TDefinition> TransitionLoadedAsync(
        DefinitionCoordinates coordinates,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom,
        DateTimeOffset transitionedAt,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(coordinates, cancellationToken).ConfigureAwait(false);
        return await TransitionAsync(
            existing, coordinates, target, allowedFrom, transitionedAt, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TDefinition> TransitionAsync(
        TDefinition existing,
        DefinitionCoordinates coordinates,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom,
        CancellationToken cancellationToken)
    {
        var current = StatusOf(existing);
        if (!allowedFrom.Contains(current))
        {
            throw CreateInvalidTransitionException(existing, target, allowedFrom);
        }

        if (current == target)
        {
            return existing;
        }

        return await TransitionAsync(
            existing, coordinates, target, allowedFrom, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TDefinition> TransitionAsync(
        TDefinition existing,
        DefinitionCoordinates coordinates,
        DefinitionLifecycleStatus target,
        IReadOnlyCollection<DefinitionLifecycleStatus> allowedFrom,
        DateTimeOffset transitionedAt,
        CancellationToken cancellationToken)
    {
        var current = StatusOf(existing);
        if (!allowedFrom.Contains(current))
            throw CreateInvalidTransitionException(existing, target, allowedFrom);
        if (current == target)
            return existing;

        var transitioned = WithStatus(existing, target, transitionedAt);
        using var body = Serialize(transitioned);
        await Mutations.UpdateAsync(
            EntityIdFor(coordinates),
            AdmitEnvelope(_schema, body, $"{_kind} lifecycle transition to {target}"),
            new UpdateOptions(TransitionActor(transitioned)),
            cancellationToken).ConfigureAwait(false);
        return transitioned;
    }

    private string PublishedOperand(DefinitionIdentity identity)
    {
        var operand = new JsonObject
        {
            ["kind"] = _kind,
            [_identityProperty] = identity.Value,
            ["status"] = DefinitionLifecycleStatus.Published.ToString(),
        };
        return operand.ToJsonString();
    }

    private static string LocalPartFor(DefinitionCoordinates coordinates)
    {
        var input = Encoding.UTF8.GetBytes(NonceFor(coordinates));
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return Convert.ToHexStringLower(digest);
    }
}
