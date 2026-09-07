using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Foundation.Definitions;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// The entity-type registry (annex §3.2, D-G): shared immutable <see cref="EntityTypeSeed"/>
/// templates plus tenant-scoped <see cref="EntityType"/> rows and overrides.
/// </summary>
/// <remarks>
/// Enforces the two F2 isolation invariants: (1) seeds are immutable shared templates — a tenant
/// edit produces a tenant-scoped override row, never a seed mutation; (2) a type's property-form
/// binding is only ever reachable through a tenant-authorized lookup (holding a
/// <c>FormDefinitionId</c> hash confers nothing).
/// </remarks>
public interface IEntityTypeRegistry
{
    /// <summary>
    /// Registers a shared immutable seed template (pack/base boot-time config). Rejects a non-seed
    /// provenance, a trait-less type, and a duplicate seed id (seeds cannot be re-seeded / mutated).
    /// </summary>
    void SeedType(EntityTypeSeed seed);

    /// <summary>
    /// Reversibly hides a Pack-provenance seed from runtime reads without deleting the immutable
    /// seed or any tenant override rows derived from it. Returns <see langword="true"/> only when
    /// this call changed a visible Pack seed to retracted; repeated calls and unknown ids are
    /// idempotent no-ops. Base seeds cannot be retracted.
    /// </summary>
    bool RetractPackSeed(EntityTypeId id);

    /// <summary>
    /// Restores a retained, retracted Pack seed to runtime reads. Returns <see langword="true"/>
    /// only when this call restored visibility; visible and unknown ids are idempotent no-ops.
    /// </summary>
    bool RestorePackSeed(EntityTypeId id);

    /// <summary>Reads a shared seed template (visible to every tenant); null when unknown.</summary>
    EntityTypeSeed? GetSeed(EntityTypeId id);

    /// <summary>All shared seed templates.</summary>
    IReadOnlyList<EntityTypeSeed> ListSeeds();

    /// <summary>
    /// Creates a tenant's own greenfield type row. Rejects the system/default tenant sentinel, a
    /// seed-layer provenance, and a trait-less type. Emits an
    /// <see cref="Audit.RegistryOp.EntityTypeCreated"/> audit event.
    /// </summary>
    Task<EntityType> CreateTypeAsync(
        EntityType type, Instant at, string? actorRef = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a <b>new tenant-scoped override row</b> of a shared seed (F2 invariant 1) — the seed
    /// is never mutated. The override keeps the seed's id, sets <see cref="EntityType.OverrideOf"/>
    /// to the seed id, and carries the tenant's replacement descriptor. Emits an
    /// <see cref="Audit.RegistryOp.EntityTypeOverridden"/> audit event.
    /// </summary>
    Task<EntityType> OverrideSeedAsync(
        TenantId tenant,
        EntityTypeId seedId,
        EntityTypeDescriptor overrideDescriptor,
        Instant at,
        CascadeLayer layer = CascadeLayer.Tenant,
        string? actorRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Reverts</b> a tenant's override row of a shared seed (the inverse of
    /// <see cref="OverrideSeedAsync"/>) — removes the tenant-scoped override so the shared seed is
    /// once again the tenant's effective type. Returns <see langword="true"/> when an override was
    /// removed; <see langword="false"/> when the tenant had no override for that seed (idempotent
    /// no-op). Rejects the system/default tenant sentinel, an unknown seed id, and a tenant row that
    /// is a greenfield type rather than an override of that seed (a greenfield type is deleted through
    /// its own path, never "reverted to a seed it never derived from"). Emits an
    /// <see cref="Audit.RegistryOp.EntityTypeOverrideReverted"/> audit event when it removes a row.
    /// The seed itself is never mutated (F2 invariant 1).
    /// </summary>
    Task<bool> RevertOverrideAsync(
        TenantId tenant, EntityTypeId seedId, Instant at, string? actorRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a tenant's own type row (override or greenfield); null when the tenant has none.</summary>
    Task<EntityType?> GetTypeAsync(TenantId tenant, EntityTypeId id, CancellationToken cancellationToken = default);

    /// <summary>All of a tenant's own type rows (does not include shared seeds).</summary>
    Task<IReadOnlyList<EntityType>> ListTypesAsync(TenantId tenant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a type's <b>effective</b> property-form binding for a tenant (F2 invariant 2): the
    /// tenant's override binding if present, else the shared seed's binding, else null. There is no
    /// API that resolves a binding from a bare <c>FormDefinitionId</c> — access is always mediated by
    /// a tenant-authorized type id, so knowing a content hash grants nothing.
    /// </summary>
    Task<FormBindingRef?> TryResolvePropertyFormAsync(
        TenantId tenant, EntityTypeId typeId, CancellationToken cancellationToken = default);
}
