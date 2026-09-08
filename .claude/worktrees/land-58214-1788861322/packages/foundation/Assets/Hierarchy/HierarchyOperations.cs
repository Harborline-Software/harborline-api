using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.Foundation.Assets.Hierarchy;

/// <summary>
/// Public hierarchy facade. Every composite is delegated intact to the single admitted coordinator;
/// this type owns no raw mutation port and cannot authorize or commit a partial operation.
/// </summary>
public sealed class HierarchyOperations(IHierarchyCompositeCoordinator coordinator)
{
    public Task<SplitResult> SplitAsync(
        EntityId oldEntity,
        IReadOnlyList<SplitTarget> newEntities,
        IReadOnlyDictionary<EntityId, EntityId> childReassignments,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default) =>
        coordinator.SplitAsync(
            oldEntity, newEntities, childReassignments, justification, actor, tenant, effectiveAt, ct);

    public Task<MergeResult> MergeAsync(
        IReadOnlyList<EntityId> oldEntities,
        SchemaId newSchema,
        JsonDocument newBody,
        CreateOptions newOptions,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default) =>
        coordinator.MergeAsync(
            oldEntities, newSchema, newBody, newOptions, justification, actor, tenant, effectiveAt, ct);

    public Task ReparentAsync(
        EntityId child,
        EntityId oldParent,
        EntityId newParent,
        string justification,
        ActorId actor,
        TenantId tenant,
        DateTimeOffset effectiveAt,
        CancellationToken ct = default) =>
        coordinator.ReparentAsync(
            child, oldParent, newParent, justification, actor, tenant, effectiveAt, ct);
}

public sealed record SplitTarget(SchemaId Schema, JsonDocument Body, CreateOptions Options);

public sealed record SplitResult(
    EntityId OldEntity,
    IReadOnlyList<EntityId> NewEntities,
    IReadOnlyList<EntityId> ReassignedChildren);

public sealed record MergeResult(
    EntityId NewEntity,
    IReadOnlyList<EntityId> OldEntities,
    IReadOnlyList<EntityId> ReassignedChildren);
