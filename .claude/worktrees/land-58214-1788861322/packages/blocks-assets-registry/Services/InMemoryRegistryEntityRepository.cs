using System.Collections.Concurrent;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Thread-safe in-memory <see cref="IRegistryEntityRepository"/>. Keyed by <c>(tenant, id)</c>;
/// every mutation rides the audit log (Wave-1 X-AUDIT durable-layer constraint). Mirrors the
/// concrete Asset domain's <c>InMemoryAssetRepository</c>.
/// </summary>
public sealed class InMemoryRegistryEntityRepository : IRegistryEntityRepository
{
    private readonly ConcurrentDictionary<(TenantId Tenant, RegistryEntityId Id), RegistryEntity> _store = new();
    private readonly IRegistryAuditLog _audit;

    /// <summary>Creates a repository wired to the given audit log.</summary>
    public InMemoryRegistryEntityRepository(IRegistryAuditLog audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public Task<RegistryEntity?> GetByIdAsync(TenantId tenant, RegistryEntityId id, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        _store.TryGetValue((tenant, id), out var entity);
        return Task.FromResult(entity);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RegistryEntity>> ListByTenantAsync(
        TenantId tenant, bool includeRetired = false, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        var query = _store.Where(kvp => kvp.Key.Tenant.Equals(tenant)).Select(kvp => kvp.Value);
        if (!includeRetired)
        {
            query = query.Where(e => e.RetiredAt is null);
        }

        IReadOnlyList<RegistryEntity> result = query.ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RegistryEntity>> ListByTypeAsync(
        TenantId tenant, EntityTypeId type, bool includeRetired = false, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        var query = _store
            .Where(kvp => kvp.Key.Tenant.Equals(tenant) && kvp.Value.Type.Equals(type))
            .Select(kvp => kvp.Value);
        if (!includeRetired)
        {
            query = query.Where(e => e.RetiredAt is null);
        }

        IReadOnlyList<RegistryEntity> result = query.ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task UpsertAsync(RegistryEntity entity, Instant at, string? actorRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        RegistryTenantGuard.Require(entity.TenantId);

        var key = (entity.TenantId, entity.Id);
        var existed = _store.ContainsKey(key);
        _store[key] = entity;

        _audit.Append(entity.TenantId, entity.Id.Value,
            existed ? RegistryOp.EntityUpdated : RegistryOp.EntityCreated, at, actorRef,
            detail: $"type={entity.Type}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RetireAsync(
        TenantId tenant, RegistryEntityId id, Instant at, string? actorRef = null, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        if (_store.TryGetValue((tenant, id), out var existing) && existing.RetiredAt is null)
        {
            _store[(tenant, id)] = existing with { RetiredAt = at };
            _audit.Append(tenant, id.Value, RegistryOp.EntityRetired, at, actorRef);
        }

        return Task.CompletedTask;
    }
}
