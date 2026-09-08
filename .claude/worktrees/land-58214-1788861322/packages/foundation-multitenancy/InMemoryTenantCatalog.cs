using System.Collections.Concurrent;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.MultiTenancy;

/// <summary>
/// Default in-memory implementation of <see cref="ITenantCatalog"/>,
/// <see cref="ITenantResolver"/>, and <see cref="ITenantLifecycleService"/>.
/// Suitable for tests, demos, lite-mode deployments, and accelerator startup
/// seed. Safe for concurrent reads after registration completes. A single
/// backing store keeps the read, resolve, and lifecycle-write views consistent.
/// </summary>
public sealed class InMemoryTenantCatalog : ITenantCatalog, ITenantResolver, ITenantLifecycleService
{
    private readonly ConcurrentDictionary<TenantId, TenantMetadata> _byId = new();
    private readonly List<TenantId> _registrationOrder = new();
    private readonly object _orderLock = new();

    /// <summary>Registers a tenant. Duplicate ids throw.</summary>
    public void Register(TenantMetadata tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (!_byId.TryAdd(tenant.Id, tenant))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenant.Id}' is already registered.");
        }

        lock (_orderLock)
        {
            _registrationOrder.Add(tenant.Id);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TenantMetadata>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        TenantMetadata[] snapshot;
        lock (_orderLock)
        {
            snapshot = _registrationOrder.Select(id => _byId[id]).ToArray();
        }

        return ValueTask.FromResult<IReadOnlyList<TenantMetadata>>(snapshot);
    }

    /// <inheritdoc />
    public ValueTask<TenantMetadata?> TryGetAsync(TenantId id, CancellationToken cancellationToken = default)
    {
        _byId.TryGetValue(id, out var metadata);
        return ValueTask.FromResult(metadata);
    }

    /// <inheritdoc />
    public ValueTask<TenantMetadata?> ResolveAsync(string candidateIdentifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(candidateIdentifier))
        {
            return ValueTask.FromResult<TenantMetadata?>(null);
        }

        _byId.TryGetValue(new TenantId(candidateIdentifier), out var metadata);
        return ValueTask.FromResult(metadata);
    }

    /// <inheritdoc />
    public ValueTask<TenantMetadata?> ArchiveTenantAsync(TenantId id, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TransitionStatus(id, TenantStatus.Archived));

    /// <inheritdoc />
    public ValueTask<TenantMetadata?> RestoreTenantAsync(TenantId id, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TransitionStatus(id, TenantStatus.Active));

    private TenantMetadata? TransitionStatus(TenantId id, TenantStatus target)
    {
        if (!_byId.TryGetValue(id, out var existing))
        {
            return null;
        }

        var updated = existing with { Status = target };
        _byId[id] = updated;
        return updated;
    }
}
