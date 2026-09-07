using System.Collections.Concurrent;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// In-memory <see cref="ILegalEntityRepository"/> backed by
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. v1 implementation suitable
/// for the desktop / kitchen-sink / signal-bridge in-memory posture. A
/// SQLite-backed implementation lands when the financial persistence hand-off
/// promotes the in-memory v1 to a real store.
/// </summary>
/// <remarks>
/// Both stores are keyed by a composite <c>(TenantId, Id)</c> so tenant
/// isolation holds by construction: caller-supplied string ids (the public
/// <see cref="LegalEntityId"/> surface allows implicit string conversion) that
/// collide across tenants cannot clobber each other.
/// </remarks>
public sealed class InMemoryLegalEntityRepository : ILegalEntityRepository
{
    private readonly ConcurrentDictionary<(TenantId, LegalEntityId), LegalEntity> _entities = new();
    private readonly ConcurrentDictionary<(TenantId, LegalEntityOwnershipId), LegalEntityOwnership> _ownerships = new();

    /// <inheritdoc />
    public Task AddEntityAsync(LegalEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.TenantId == default) throw new ArgumentException("TenantId is required.", nameof(entity));
        if (string.IsNullOrWhiteSpace(entity.LegalName)) throw new ArgumentException("LegalName is required.", nameof(entity));
        _entities[(entity.TenantId, entity.Id)] = entity;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LegalEntity?> GetEntityAsync(TenantId tenantId, LegalEntityId id, CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        var entity = _entities.TryGetValue((tenantId, id), out var found) ? found : null;
        return Task.FromResult(entity);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LegalEntity>> ListEntitiesAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        IReadOnlyList<LegalEntity> result = _entities.Values
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Id.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task AddOwnershipAsync(LegalEntityOwnership ownership, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (ownership.TenantId == default) throw new ArgumentException("TenantId is required.", nameof(ownership));
        if (ownership.ParentEntityId == ownership.OwnedEntityId)
            throw new ArgumentException("An entity cannot own itself.", nameof(ownership));
        if (ownership.OwnershipPercent <= 0m || ownership.OwnershipPercent > 100m)
            throw new ArgumentException("OwnershipPercent must be in (0, 100].", nameof(ownership));

        if (!_entities.ContainsKey((ownership.TenantId, ownership.ParentEntityId)))
            throw new ArgumentException("Parent entity does not exist in the tenant.", nameof(ownership));
        if (!_entities.ContainsKey((ownership.TenantId, ownership.OwnedEntityId)))
            throw new ArgumentException("Owned entity does not exist in the tenant.", nameof(ownership));

        _ownerships[(ownership.TenantId, ownership.Id)] = ownership;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LegalEntityOwnership>> ListOwnershipsAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        IReadOnlyList<LegalEntityOwnership> result = _ownerships.Values
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Id.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<ConsolidationScope> GetConsolidationScopeAsync(TenantId tenantId, LegalEntityId rootEntityId, CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));

        if (!_entities.TryGetValue((tenantId, rootEntityId), out var root))
            throw new ArgumentException("Root entity does not exist in the tenant.", nameof(rootEntityId));

        var tenantOwnerships = _ownerships.Values.Where(x => x.TenantId == tenantId).ToList();

        // BFS over the ownership graph: every entity transitively owned from the
        // root is a consolidated subsidiary (ADR 0104 §5). Visited-set guards
        // against cycles in malformed graphs.
        var consolidated = new HashSet<LegalEntityId>();
        var queue = new Queue<LegalEntityId>();
        queue.Enqueue(rootEntityId);
        var visited = new HashSet<LegalEntityId> { rootEntityId };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in tenantOwnerships.Where(e => e.ParentEntityId == current))
            {
                if (!visited.Add(edge.OwnedEntityId)) continue;
                consolidated.Add(edge.OwnedEntityId);
                queue.Enqueue(edge.OwnedEntityId);
            }
        }

        // Common-control siblings: same non-null CommonControlGroupId as the
        // root, not the root, and not already consolidated by ownership.
        // Presented combined (summed side-by-side), not eliminated (ADR 0104 §5).
        var combined = new HashSet<LegalEntityId>();
        if (!string.IsNullOrEmpty(root.CommonControlGroupId))
        {
            foreach (var entity in _entities.Values.Where(e => e.TenantId == tenantId))
            {
                if (entity.Id == rootEntityId) continue;
                if (consolidated.Contains(entity.Id)) continue;
                if (entity.CommonControlGroupId == root.CommonControlGroupId)
                    combined.Add(entity.Id);
            }
        }

        // Deterministic ordering: root first, then consolidated (by id), then
        // combined (by id).
        var members = new List<ConsolidationMember>
        {
            new(rootEntityId, ConsolidationPresentation.Consolidated),
        };
        members.AddRange(consolidated
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new ConsolidationMember(id, ConsolidationPresentation.Consolidated)));
        members.AddRange(combined
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new ConsolidationMember(id, ConsolidationPresentation.Combined)));

        return Task.FromResult(new ConsolidationScope(rootEntityId, members));
    }
}
