using Harborline.Api.Blocks.Assets.Registry.Model;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Tenant-scoped store for typed dated <see cref="TypedRelationship"/> edges (annex §3.1). The
/// containment (<see cref="RelationshipKind.Contains"/>) relation is cycle-guarded and
/// depth-bounded; hierarchy and location are exposed only as VIEWS via explicit as-of-clock
/// queries (ADR 0101 Rev 3.1 / A5).
/// </summary>
public interface ITypedRelationshipStore
{
    /// <summary>The maximum containment depth (root → leaf) the store will allow (A5b).</summary>
    int MaxContainmentDepth { get; }

    /// <summary>
    /// Adds an edge. Rejects (fail-closed):
    /// the system/default tenant sentinel (A5a); a self-edge; an edge whose endpoints are not both
    /// present under the edge's tenant (forbids cross-tenant edges — A5a); an inverted effective
    /// window; and, for containment, any edge that would create a cycle or exceed
    /// <see cref="MaxContainmentDepth"/> in the graph effective at <see cref="TypedRelationship.EffectiveFrom"/>
    /// (A5b). Emits a <see cref="Audit.RegistryOp.RelationshipAdded"/> audit event.
    /// </summary>
    Task<TypedRelationship> AddAsync(TypedRelationship edge, string? actorRef = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes an edge by setting its <see cref="TypedRelationship.EffectiveTo"/> to
    /// <paramref name="effectiveTo"/>. Emits a <see cref="Audit.RegistryOp.RelationshipClosed"/>
    /// event. Rejects an <paramref name="effectiveTo"/> before the edge's start.
    /// </summary>
    Task CloseAsync(
        TenantId tenant, TypedRelationshipId id, Instant effectiveTo, string? actorRef = null, CancellationToken cancellationToken = default);

    /// <summary>Reads an edge by id within a tenant; null when absent.</summary>
    Task<TypedRelationship?> GetByIdAsync(TenantId tenant, TypedRelationshipId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Edges touching <paramref name="entity"/> (as either endpoint) of the given kind (or any kind
    /// when null), effective at <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyList<TypedRelationship>> GetEdgesAsAtAsync(
        TenantId tenant, RegistryEntityId entity, RelationshipKind? kind, Instant asOf, CancellationToken cancellationToken = default);

    /// <summary>The container of <paramref name="child"/> at <paramref name="asOf"/>, or null if none.</summary>
    Task<RegistryEntityId?> GetContainerAsAtAsync(
        TenantId tenant, RegistryEntityId child, Instant asOf, CancellationToken cancellationToken = default);

    /// <summary>The direct contents of <paramref name="container"/> at <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<RegistryEntityId>> GetContentsAsAtAsync(
        TenantId tenant, RegistryEntityId container, Instant asOf, CancellationToken cancellationToken = default);

    /// <summary>
    /// The root → <paramref name="entity"/> containment path at <paramref name="asOf"/> (the
    /// "where is it" view), from the outermost container to the entity itself.
    /// </summary>
    Task<IReadOnlyList<RegistryEntityId>> GetContainmentPathAsAtAsync(
        TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default);

    /// <summary>
    /// The place a movable <paramref name="entity"/> is located at <paramref name="asOf"/> via its
    /// dated <see cref="RelationshipKind.LocatedAt"/> edge, or null if none.
    /// </summary>
    Task<RegistryEntityId?> GetLocationAsAtAsync(
        TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default);
}
