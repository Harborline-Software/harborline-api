using System.Runtime.CompilerServices;
using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Temporal;

namespace Harborline.Api.Foundation.Assets.Hierarchy;

/// <summary>
/// Zero-dependency in-memory <see cref="IHierarchyService"/>.
/// </summary>
/// <remarks>
/// Maintains an append-only list of <see cref="EntityEdge"/> rows and a synchronously-updated
/// closure table. Plan D-HIERARCHY.
/// </remarks>
public sealed class InMemoryHierarchyService : IHierarchyService, IHierarchyMutationStore, IHierarchyCompositeUnitOfWork
{
    private readonly InMemoryAssetStorage? _assetStorage;
    private readonly object _lock = new();
    private readonly List<EntityEdge> _edges = new();
    private readonly List<ClosureEntry> _closure = new();
    private long _nextEdgeId;

    public InMemoryHierarchyService(InMemoryAssetStorage? assetStorage = null)
    {
        _assetStorage = assetStorage;
    }

    public async Task<T> ExecuteAtomicAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var storage = _assetStorage
            ?? throw new InvalidOperationException("Hierarchy composites require the shared asset-storage unit of work.");
        return await storage.ExecuteExclusiveAsync(async () =>
        {
            var entities = storage.Entities.ToDictionary(pair => pair.Key, pair => Clone(pair.Value));
            var versions = storage.Versions.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
            var audit = storage.Audit.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
            List<EntityEdge> edges;
            List<ClosureEntry> closure;
            long nextEdgeId;
            lock (_lock)
            {
                edges = _edges.ToList();
                closure = _closure.ToList();
                nextEdgeId = _nextEdgeId;
            }
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch
            {
                Restore(storage.Entities, entities);
                Restore(storage.Versions, versions);
                Restore(storage.Audit, audit);
                lock (_lock)
                {
                    _edges.Clear();
                    _edges.AddRange(edges);
                    _closure.Clear();
                    _closure.AddRange(closure);
                    _nextEdgeId = nextEdgeId;
                }
                throw;
            }
        }, ct).ConfigureAwait(false);
    }

    private static EntityRecord Clone(EntityRecord value) => new()
    {
        Id = value.Id,
        Schema = value.Schema,
        Tenant = value.Tenant,
        CurrentVersion = value.CurrentVersion,
        BodyJson = value.BodyJson,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt,
        DeletedAt = value.DeletedAt,
        CreationNonce = value.CreationNonce,
        CreationIssuer = value.CreationIssuer,
        Binding = value.Binding,
    };

    private static void Restore<TKey, TValue>(
        System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue> target,
        IReadOnlyDictionary<TKey, TValue> snapshot)
        where TKey : notnull
    {
        target.Clear();
        foreach (var pair in snapshot) target[pair.Key] = pair.Value;
    }

    /// <inheritdoc />
    public Task<EntityEdge> AddEdgeAsync(
        EntityId from,
        EntityId to,
        EdgeKind kind,
        DateTimeOffset validFrom,
        JsonDocument? metadata = null,
        CancellationToken ct = default) => ExecuteStorageExclusiveAsync(
            () => AddEdgeCoreAsync(from, to, kind, validFrom, metadata), ct);

    private Task<EntityEdge> AddEdgeCoreAsync(
        EntityId from,
        EntityId to,
        EdgeKind kind,
        DateTimeOffset validFrom,
        JsonDocument? metadata)
    {
        lock (_lock)
        {
            var id = Interlocked.Increment(ref _nextEdgeId);
            var edge = new EntityEdge(id, from, to, kind, new TemporalRange(validFrom, null), metadata);
            _edges.Add(edge);

            if (kind == EdgeKind.ChildOf)
            {
                // Closure is built as: ancestor → descendant, where `to` is the parent and `from` is the child.
                // (Plan semantics: AddEdgeAsync(child, parent, ChildOf) — i.e. `from` is the child
                // and `to` is the parent. We keep that interpretation consistent across the codebase.)
                // Ensure self-rows for both endpoints.
                EnsureSelfRow(from, validFrom);
                EnsureSelfRow(to, validFrom);

                // For every ancestor A of `to` (including `to` itself), and every descendant D of `from`
                // (including `from` itself), add closure (A, D, depthA→to + 1 + depthFrom→D).
                var parentAncestors = _closure
                    .Where(c => c.Descendant == to && c.Validity.IsValidAt(validFrom))
                    .ToList();
                var childDescendants = _closure
                    .Where(c => c.Ancestor == from && c.Validity.IsValidAt(validFrom))
                    .ToList();

                foreach (var pa in parentAncestors)
                {
                    foreach (var cd in childDescendants)
                    {
                        var newDepth = pa.Depth + 1 + cd.Depth;
                        // Avoid duplicate active rows with the same (ancestor, descendant, depth).
                        bool exists = _closure.Any(x =>
                            x.Ancestor == pa.Ancestor &&
                            x.Descendant == cd.Descendant &&
                            x.Depth == newDepth &&
                            x.Validity.IsValidAt(validFrom));
                        if (!exists)
                        {
                            _closure.Add(new ClosureEntry(
                                pa.Ancestor,
                                cd.Descendant,
                                newDepth,
                                new TemporalRange(validFrom, null)));
                        }
                    }
                }
            }

            return Task.FromResult(edge);
        }
    }

    private void EnsureSelfRow(EntityId entity, DateTimeOffset validFrom)
    {
        if (!_closure.Any(c => c.Ancestor == entity && c.Descendant == entity && c.Depth == 0))
            _closure.Add(new ClosureEntry(entity, entity, 0, new TemporalRange(DateTimeOffset.MinValue, null)));
    }

    /// <inheritdoc />
    public Task InvalidateEdgeAsync(long edgeId, DateTimeOffset validTo, CancellationToken ct = default) =>
        ExecuteStorageExclusiveAsync(() => InvalidateEdgeCoreAsync(edgeId, validTo), ct);

    private Task InvalidateEdgeCoreAsync(long edgeId, DateTimeOffset validTo)
    {
        lock (_lock)
        {
            var idx = _edges.FindIndex(e => e.Id == edgeId);
            if (idx < 0) throw new InvalidOperationException($"Edge {edgeId} not found.");
            var edge = _edges[idx];
            if (edge.Validity.ValidTo is { } existingEnd && existingEnd <= validTo)
                return Task.CompletedTask;

            _edges[idx] = edge with { Validity = new TemporalRange(edge.Validity.ValidFrom, validTo) };

            if (edge.Kind == EdgeKind.ChildOf)
            {
                // The edge connects child=from → parent=to. Its disappearance affects every
                // closure row where ancestor ∈ ancestors-of(parent) and descendant ∈ descendants-of(child).
                // We keep it simple: for every still-open closure row whose ancestor is an ancestor of `to`
                // (or is `to` itself) AND whose descendant is a descendant of `from` (or is `from` itself)
                // AND depth > 0, close it.
                var ancestorsOfParent = _closure
                    .Where(c => c.Descendant == edge.To && c.Validity.IsValidAt(validTo))
                    .Select(c => c.Ancestor)
                    .ToHashSet();
                var descendantsOfChild = _closure
                    .Where(c => c.Ancestor == edge.From && c.Validity.IsValidAt(validTo))
                    .Select(c => c.Descendant)
                    .ToHashSet();

                for (int i = 0; i < _closure.Count; i++)
                {
                    var row = _closure[i];
                    if (row.Depth == 0) continue;
                    if (row.Validity.ValidTo is { } rowExistingEnd && rowExistingEnd <= validTo) continue;
                    if (!ancestorsOfParent.Contains(row.Ancestor)) continue;
                    if (!descendantsOfChild.Contains(row.Descendant)) continue;
                    _closure[i] = row with { Validity = new TemporalRange(row.Validity.ValidFrom, validTo) };
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EntityEdge> GetChildrenAsync(EntityId parent, DateTimeOffset? asOf = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var t = asOf ?? DateTimeOffset.UtcNow;
        var snapshot = await ExecuteStorageExclusiveAsync(() => Task.FromResult(Children(parent, t)), ct)
            .ConfigureAwait(false);
        foreach (var edge in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return edge;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EntityEdge> GetChildrenNotEndedAsync(
        EntityId parent,
        DateTimeOffset asOf,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var snapshot = await ExecuteStorageExclusiveAsync(
            () => Task.FromResult(ChildrenNotEnded(parent, asOf)), ct).ConfigureAwait(false);
        foreach (var edge in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return edge;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EntityEdge> GetParentsAsync(EntityId child, DateTimeOffset? asOf = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var t = asOf ?? DateTimeOffset.UtcNow;
        var snapshot = await ExecuteStorageExclusiveAsync(() => Task.FromResult(Parents(child, t)), ct)
            .ConfigureAwait(false);
        foreach (var edge in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return edge;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClosureEntry> GetAncestorsAsync(EntityId descendant, DateTimeOffset? asOf = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var t = asOf ?? DateTimeOffset.UtcNow;
        var snapshot = await ExecuteStorageExclusiveAsync(() => Task.FromResult(Ancestors(descendant, t)), ct)
            .ConfigureAwait(false);
        foreach (var c in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClosureEntry> GetDescendantsAsync(EntityId ancestor, DateTimeOffset? asOf = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var t = asOf ?? DateTimeOffset.UtcNow;
        var snapshot = await ExecuteStorageExclusiveAsync(() => Task.FromResult(Descendants(ancestor, t)), ct)
            .ConfigureAwait(false);
        foreach (var c in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public Task<TemporalSnapshot> GetSubtreeAsync(EntityId root, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
        ExecuteStorageExclusiveAsync(() => Task.FromResult(GetSubtree(root, asOf)), ct);

    private TemporalSnapshot GetSubtree(EntityId root, DateTimeOffset? asOf)
    {
        var t = asOf ?? DateTimeOffset.UtcNow;
        ClosureEntry[] rows;
        lock (_lock)
        {
            rows = _closure
                .Where(c => c.Ancestor == root && c.Validity.IsValidAt(t))
                .OrderBy(c => c.Depth)
                .ToArray();
        }
        return new TemporalSnapshot(root, t, rows);
    }

    private Task<T> ExecuteStorageExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct) =>
        _assetStorage is null ? action() : _assetStorage.ExecuteExclusiveAsync(action, ct);

    private Task ExecuteStorageExclusiveAsync(Func<Task> action, CancellationToken ct) =>
        _assetStorage is null ? action() : _assetStorage.ExecuteExclusiveAsync(action, ct);

    private EntityEdge[] Children(EntityId parent, DateTimeOffset at)
    {
        lock (_lock)
            return _edges.Where(e => e.Kind == EdgeKind.ChildOf && e.To == parent && e.Validity.IsValidAt(at)).ToArray();
    }

    private EntityEdge[] ChildrenNotEnded(EntityId parent, DateTimeOffset asOf)
    {
        lock (_lock)
            return _edges.Where(e => e.Kind == EdgeKind.ChildOf
                && e.To == parent
                && (e.Validity.ValidTo is null || e.Validity.ValidTo > asOf)).ToArray();
    }

    private EntityEdge[] Parents(EntityId child, DateTimeOffset at)
    {
        lock (_lock)
            return _edges.Where(e => e.Kind == EdgeKind.ChildOf && e.From == child && e.Validity.IsValidAt(at)).ToArray();
    }

    private ClosureEntry[] Ancestors(EntityId descendant, DateTimeOffset at)
    {
        lock (_lock)
            return _closure.Where(c => c.Descendant == descendant && c.Depth > 0 && c.Validity.IsValidAt(at))
                .OrderBy(c => c.Depth).ToArray();
    }

    private ClosureEntry[] Descendants(EntityId ancestor, DateTimeOffset at)
    {
        lock (_lock)
            return _closure.Where(c => c.Ancestor == ancestor && c.Depth > 0 && c.Validity.IsValidAt(at))
                .OrderBy(c => c.Depth).ToArray();
    }

    /// <summary>Internal helper for <c>HierarchyOperations</c> to enumerate edges by destination.</summary>
    internal IReadOnlyList<EntityEdge> GetOutgoingActiveChildEdges(EntityId parent, DateTimeOffset at)
    {
        lock (_lock)
        {
            return _edges
                .Where(e => e.Kind == EdgeKind.ChildOf && e.To == parent && e.Validity.IsValidAt(at))
                .ToList();
        }
    }
}

/// <summary>Read-only facade that prevents a resolved hierarchy reader from exposing raw edge mutations.</summary>
public sealed class InMemoryHierarchyServiceReader(InMemoryHierarchyService inner) : IHierarchyService
{
    public IAsyncEnumerable<EntityEdge> GetChildrenAsync(
        EntityId parent, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
        inner.GetChildrenAsync(parent, asOf, ct);

    public IAsyncEnumerable<EntityEdge> GetChildrenNotEndedAsync(
        EntityId parent, DateTimeOffset asOf, CancellationToken ct = default) =>
        inner.GetChildrenNotEndedAsync(parent, asOf, ct);

    public IAsyncEnumerable<EntityEdge> GetParentsAsync(
        EntityId child, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
        inner.GetParentsAsync(child, asOf, ct);

    public IAsyncEnumerable<ClosureEntry> GetAncestorsAsync(
        EntityId descendant, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
        inner.GetAncestorsAsync(descendant, asOf, ct);

    public IAsyncEnumerable<ClosureEntry> GetDescendantsAsync(
        EntityId ancestor, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
        inner.GetDescendantsAsync(ancestor, asOf, ct);

    public Task<TemporalSnapshot> GetSubtreeAsync(
        EntityId root, DateTimeOffset? asOf = null, CancellationToken ct = default) =>
        inner.GetSubtreeAsync(root, asOf, ct);
}
