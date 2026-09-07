using System.Collections.Concurrent;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Thread-safe in-memory <see cref="ITypedRelationshipStore"/>. Enforces the A5 invariants:
/// same-tenant edges (endpoint ownership verified against the entity repository), a fail-closed
/// sentinel reject, and a cycle-guard + depth-bound on containment evaluated in the graph effective
/// at the new edge's start instant. All read views take an explicit as-of clock — never ambient now.
/// </summary>
public sealed class InMemoryTypedRelationshipStore : ITypedRelationshipStore
{
    /// <summary>Default containment depth bound (root → leaf edges).</summary>
    public const int DefaultMaxContainmentDepth = 32;

    private readonly ConcurrentDictionary<(TenantId Tenant, TypedRelationshipId Id), TypedRelationship> _store = new();
    private readonly IRegistryEntityRepository _entities;
    private readonly IRegistryAuditLog _audit;
    private readonly object _writeGate = new();

    /// <inheritdoc />
    public int MaxContainmentDepth { get; }

    /// <summary>Creates a store wired to the entity repository (endpoint checks) and audit log.</summary>
    public InMemoryTypedRelationshipStore(
        IRegistryEntityRepository entities, IRegistryAuditLog audit, int maxContainmentDepth = DefaultMaxContainmentDepth)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        if (maxContainmentDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContainmentDepth), maxContainmentDepth,
                "Containment depth bound must be at least 1.");
        }

        MaxContainmentDepth = maxContainmentDepth;
    }

    /// <inheritdoc />
    public async Task<TypedRelationship> AddAsync(
        TypedRelationship edge, string? actorRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        RegistryTenantGuard.Require(edge.TenantId);

        if (edge.From.Equals(edge.To))
        {
            throw new ArgumentException("A relationship cannot connect an entity to itself.", nameof(edge));
        }

        if (edge.EffectiveTo is { } to && to.Value <= edge.EffectiveFrom.Value)
        {
            throw new ArgumentException(
                "EffectiveTo must be strictly after EffectiveFrom.", nameof(edge));
        }

        // Same-tenant endpoints (A5a): both endpoints must exist under the edge's tenant. An entity
        // owned by another tenant is not found here, so a cross-tenant edge is impossible.
        await RequireEndpointInTenantAsync(edge.TenantId, edge.From, cancellationToken).ConfigureAwait(false);
        await RequireEndpointInTenantAsync(edge.TenantId, edge.To, cancellationToken).ConfigureAwait(false);

        // Containment guard (A5b) + persist under one lock so concurrent adds can't race a cycle in.
        lock (_writeGate)
        {
            if (edge.Kind == RelationshipKind.Contains)
            {
                GuardContainment(edge);
            }

            _store[(edge.TenantId, edge.Id)] = edge;
        }

        _audit.Append(edge.TenantId, edge.Id.Value, RegistryOp.RelationshipAdded, edge.EffectiveFrom, actorRef,
            detail: $"{edge.Kind}:{edge.From}->{edge.To}");
        return edge;
    }

    /// <inheritdoc />
    public Task CloseAsync(
        TenantId tenant, TypedRelationshipId id, Instant effectiveTo, string? actorRef = null, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        lock (_writeGate)
        {
            if (_store.TryGetValue((tenant, id), out var edge))
            {
                if (effectiveTo.Value <= edge.EffectiveFrom.Value)
                {
                    throw new ArgumentException(
                        "EffectiveTo must be strictly after the edge's EffectiveFrom.", nameof(effectiveTo));
                }

                _store[(tenant, id)] = edge with { EffectiveTo = effectiveTo };
                _audit.Append(tenant, id.Value, RegistryOp.RelationshipClosed, effectiveTo, actorRef);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TypedRelationship?> GetByIdAsync(TenantId tenant, TypedRelationshipId id, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        _store.TryGetValue((tenant, id), out var edge);
        return Task.FromResult(edge);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TypedRelationship>> GetEdgesAsAtAsync(
        TenantId tenant, RegistryEntityId entity, RelationshipKind? kind, Instant asOf, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        IReadOnlyList<TypedRelationship> result = EffectiveEdges(tenant, asOf)
            .Where(e => (e.From.Equals(entity) || e.To.Equals(entity)) && (kind is null || e.Kind == kind))
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RegistryEntityId?> GetContainerAsAtAsync(
        TenantId tenant, RegistryEntityId child, Instant asOf, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        return Task.FromResult(ContainerOf(tenant, child, asOf));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RegistryEntityId>> GetContentsAsAtAsync(
        TenantId tenant, RegistryEntityId container, Instant asOf, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        IReadOnlyList<RegistryEntityId> result = EffectiveContains(tenant, asOf)
            .Where(e => e.From.Equals(container))
            .Select(e => e.To)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RegistryEntityId>> GetContainmentPathAsAtAsync(
        TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        var path = new List<RegistryEntityId> { entity };
        var seen = new HashSet<RegistryEntityId> { entity };
        var current = entity;

        // Walk up; the cycle-guard makes this terminate, but bound it defensively anyway.
        for (var hops = 0; hops <= MaxContainmentDepth; hops++)
        {
            var container = ContainerOf(tenant, current, asOf);
            if (container is not { } parent || !seen.Add(parent))
            {
                break;
            }

            path.Add(parent);
            current = parent;
        }

        path.Reverse(); // root → entity
        IReadOnlyList<RegistryEntityId> result = path;
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RegistryEntityId?> GetLocationAsAtAsync(
        TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        var location = EffectiveEdges(tenant, asOf)
            .Where(e => e.Kind == RelationshipKind.LocatedAt && e.From.Equals(entity))
            .OrderBy(e => e.EffectiveFrom.Value)
            .ThenBy(e => e.Id.Value, StringComparer.Ordinal)
            .Select(e => (RegistryEntityId?)e.To)
            .FirstOrDefault();
        return Task.FromResult(location);
    }

    private async Task RequireEndpointInTenantAsync(TenantId tenant, RegistryEntityId endpoint, CancellationToken ct)
    {
        var entity = await _entities.GetByIdAsync(tenant, endpoint, ct).ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException(
                $"Edge endpoint '{endpoint}' is not an entity of tenant '{tenant}'. Edges are "
                + "same-tenant only (A5a); a cross-tenant or dangling endpoint is rejected fail-closed.");
        }
    }

    // --- containment graph helpers (all evaluated on the effective-at-instant graph) ---

    private IEnumerable<TypedRelationship> EffectiveEdges(TenantId tenant, Instant asOf) =>
        _store.Values.Where(e => e.TenantId.Equals(tenant) && e.IsEffectiveAt(asOf));

    private IEnumerable<TypedRelationship> EffectiveContains(TenantId tenant, Instant asOf) =>
        EffectiveEdges(tenant, asOf).Where(e => e.Kind == RelationshipKind.Contains);

    private RegistryEntityId? ContainerOf(TenantId tenant, RegistryEntityId child, Instant asOf) =>
        EffectiveContains(tenant, asOf)
            .Where(e => e.To.Equals(child))
            .OrderBy(e => e.EffectiveFrom.Value)
            .ThenBy(e => e.Id.Value, StringComparer.Ordinal)
            .Select(e => (RegistryEntityId?)e.From)
            .FirstOrDefault();

    private void GuardContainment(TypedRelationship edge)
    {
        // Evaluate against the containment graph effective at the new edge's start instant.
        var contains = EffectiveContains(edge.TenantId, edge.EffectiveFrom).ToList();

        var children = new Dictionary<RegistryEntityId, List<RegistryEntityId>>();
        var parents = new Dictionary<RegistryEntityId, List<RegistryEntityId>>();
        foreach (var e in contains)
        {
            (children.TryGetValue(e.From, out var cl) ? cl : children[e.From] = new()).Add(e.To);
            (parents.TryGetValue(e.To, out var pl) ? pl : parents[e.To] = new()).Add(e.From);
        }

        // Cycle: adding From(container) → To(child) is a cycle iff From is already a descendant of To.
        if (IsDescendant(target: edge.From, root: edge.To, children))
        {
            throw new InvalidOperationException(
                $"Adding containment '{edge.From}' contains '{edge.To}' would create a cycle: "
                + $"'{edge.From}' is already contained (transitively) by '{edge.To}'. A containment "
                + "cycle is impossible to persist (A5b).");
        }

        // Depth bound: longest chain through the new edge = ancestors(From) + this edge + subtree(To).
        var newDeepest = LongestUpward(edge.From, parents) + 1 + LongestDownward(edge.To, children);
        if (newDeepest > MaxContainmentDepth)
        {
            throw new InvalidOperationException(
                $"Adding containment '{edge.From}' contains '{edge.To}' would create a chain of "
                + $"{newDeepest} edges, exceeding the depth bound of {MaxContainmentDepth} (A5b).");
        }
    }

    private static bool IsDescendant(
        RegistryEntityId target, RegistryEntityId root, IReadOnlyDictionary<RegistryEntityId, List<RegistryEntityId>> children)
    {
        var stack = new Stack<RegistryEntityId>();
        stack.Push(root);
        var seen = new HashSet<RegistryEntityId>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            if (node.Equals(target))
            {
                return true;
            }

            if (children.TryGetValue(node, out var kids))
            {
                foreach (var kid in kids)
                {
                    stack.Push(kid);
                }
            }
        }

        return false;
    }

    private static int LongestUpward(RegistryEntityId node, IReadOnlyDictionary<RegistryEntityId, List<RegistryEntityId>> parents) =>
        LongestChain(node, parents, new HashSet<RegistryEntityId>());

    private static int LongestDownward(RegistryEntityId node, IReadOnlyDictionary<RegistryEntityId, List<RegistryEntityId>> children) =>
        LongestChain(node, children, new HashSet<RegistryEntityId>());

    private static int LongestChain(
        RegistryEntityId node, IReadOnlyDictionary<RegistryEntityId, List<RegistryEntityId>> adjacency, HashSet<RegistryEntityId> onPath)
    {
        if (!onPath.Add(node) || !adjacency.TryGetValue(node, out var next) || next.Count == 0)
        {
            onPath.Remove(node);
            return 0;
        }

        var best = 0;
        foreach (var n in next)
        {
            best = Math.Max(best, 1 + LongestChain(n, adjacency, onPath));
        }

        onPath.Remove(node);
        return best;
    }
}
