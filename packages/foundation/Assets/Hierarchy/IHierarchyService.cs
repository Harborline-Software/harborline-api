using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.Foundation.Assets.Hierarchy;

/// <summary>
/// Persistent hierarchy service.
/// </summary>
/// <remarks>
/// Exposes mutation ops for <see cref="EntityEdge"/>s plus temporal read queries that walk
/// the materialized closure table. Plan D-HIERARCHY.
/// </remarks>
public interface IHierarchyService
{
    /// <summary>Streams direct children of <paramref name="parent"/> valid at <paramref name="asOf"/>.</summary>
    IAsyncEnumerable<EntityEdge> GetChildrenAsync(EntityId parent, DateTimeOffset? asOf = null, CancellationToken ct = default);

    /// <summary>
    /// Streams direct children whose edge has not ended by <paramref name="asOf"/>, regardless of its start.
    /// </summary>
    IAsyncEnumerable<EntityEdge> GetChildrenNotEndedAsync(
        EntityId parent, DateTimeOffset asOf, CancellationToken ct = default);

    /// <summary>Streams direct parents of <paramref name="child"/> valid at <paramref name="asOf"/>.</summary>
    IAsyncEnumerable<EntityEdge> GetParentsAsync(EntityId child, DateTimeOffset? asOf = null, CancellationToken ct = default);

    /// <summary>Streams ancestor-descendant closure rows valid at <paramref name="asOf"/>.</summary>
    IAsyncEnumerable<ClosureEntry> GetAncestorsAsync(EntityId descendant, DateTimeOffset? asOf = null, CancellationToken ct = default);

    /// <summary>Streams descendant closure rows valid at <paramref name="asOf"/>.</summary>
    IAsyncEnumerable<ClosureEntry> GetDescendantsAsync(EntityId ancestor, DateTimeOffset? asOf = null, CancellationToken ct = default);

    /// <summary>Returns a full as-of snapshot of the subtree rooted at <paramref name="root"/>.</summary>
    Task<TemporalSnapshot> GetSubtreeAsync(EntityId root, DateTimeOffset? asOf = null, CancellationToken ct = default);
}

/// <summary>Unregistered raw edge persistence held only beneath admitted hierarchy coordinators.</summary>
public interface IHierarchyMutationStore : IHierarchyService
{
    Task<EntityEdge> AddEdgeAsync(
        EntityId from,
        EntityId to,
        EdgeKind kind,
        DateTimeOffset validFrom,
        JsonDocument? metadata = null,
        CancellationToken ct = default);

    Task InvalidateEdgeAsync(long edgeId, DateTimeOffset validTo, CancellationToken ct = default);
}

/// <summary>
/// Unregistered hierarchy unit-of-work face. The callback is entered only by the admitted hierarchy
/// coordinator and commits entity, edge, and asset-audit state together or restores all three.
/// </summary>
public interface IHierarchyCompositeUnitOfWork : IHierarchyMutationStore
{
    Task<T> ExecuteAtomicAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default);
}

/// <summary>The single admitted boundary for hierarchy split, merge, and reparent composites.</summary>
public interface IHierarchyCompositeCoordinator
{
    Task<SplitResult> SplitAsync(
        EntityId oldEntity,
        IReadOnlyList<SplitTarget> newEntities,
        IReadOnlyDictionary<EntityId, EntityId> childReassignments,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default);

    Task<MergeResult> MergeAsync(
        IReadOnlyList<EntityId> oldEntities,
        SchemaId newSchema,
        JsonDocument newBody,
        CreateOptions newOptions,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default);

    Task ReparentAsync(
        EntityId child,
        EntityId oldParent,
        EntityId newParent,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default);
}
