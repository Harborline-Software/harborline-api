using Harborline.Api.Blocks.Assets.Registry.Model;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Tenant-scoped repository for typed <see cref="RegistryEntity"/> instances. Every operation
/// rejects the system / default tenant sentinel and never returns another tenant's entities.
/// </summary>
public interface IRegistryEntityRepository
{
    /// <summary>Reads an entity by id within a tenant; null when absent.</summary>
    Task<RegistryEntity?> GetByIdAsync(TenantId tenant, RegistryEntityId id, CancellationToken cancellationToken = default);

    /// <summary>All of a tenant's entities (optionally including retired).</summary>
    Task<IReadOnlyList<RegistryEntity>> ListByTenantAsync(
        TenantId tenant, bool includeRetired = false, CancellationToken cancellationToken = default);

    /// <summary>All of a tenant's entities of a given type (optionally including retired).</summary>
    Task<IReadOnlyList<RegistryEntity>> ListByTypeAsync(
        TenantId tenant, EntityTypeId type, bool includeRetired = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces an entity, emitting an <see cref="Audit.RegistryOp.EntityCreated"/> or
    /// <see cref="Audit.RegistryOp.EntityUpdated"/> audit event. <paramref name="at"/> is the as-of
    /// clock supplied by the caller.
    /// </summary>
    Task UpsertAsync(RegistryEntity entity, Instant at, string? actorRef = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires (soft-deletes) an entity, emitting an <see cref="Audit.RegistryOp.EntityRetired"/>
    /// event. No-op when the entity is absent.
    /// </summary>
    Task RetireAsync(
        TenantId tenant, RegistryEntityId id, Instant at, string? actorRef = null, CancellationToken cancellationToken = default);
}
