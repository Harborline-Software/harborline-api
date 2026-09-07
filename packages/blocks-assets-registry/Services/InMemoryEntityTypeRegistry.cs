using System.Collections.Concurrent;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Foundation.Definitions;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Services;

/// <summary>
/// Thread-safe in-memory <see cref="IEntityTypeRegistry"/>. Shared seed templates live in one map;
/// tenant rows live in a tenant-keyed map — the immutable-seed / mutable-override split is
/// structural (F2 invariant 1), and property-form resolution is mediated by tenant-owned rows
/// (F2 invariant 2).
/// </summary>
public sealed class InMemoryEntityTypeRegistry : IEntityTypeRegistry
{
    private readonly ConcurrentDictionary<EntityTypeId, EntityTypeSeed> _seeds = new();
    private readonly ConcurrentDictionary<EntityTypeId, byte> _retractedPackSeeds = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, EntityTypeId Id), EntityType> _typesByTenant = new();
    private readonly IRegistryAuditLog _audit;

    /// <summary>Creates a registry wired to the given audit log for mutation journaling.</summary>
    public InMemoryEntityTypeRegistry(IRegistryAuditLog audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public void SeedType(EntityTypeSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (!seed.Provenance.IsSeed())
        {
            throw new ArgumentException(
                $"A seed template must be declared at a seed layer (Base/Pack); got {seed.Provenance}.",
                nameof(seed));
        }

        RequireAtLeastOneTrait(seed.Descriptor, nameof(seed));

        if (!_seeds.TryAdd(seed.Id, seed))
        {
            throw new InvalidOperationException(
                $"Seed type '{seed.Id}' already exists. Seeds are immutable shared templates and "
                + "cannot be re-seeded (F2 invariant 1); a tenant edit must go through OverrideSeedAsync.");
        }
    }

    /// <inheritdoc />
    public bool RetractPackSeed(EntityTypeId id)
    {
        if (!_seeds.TryGetValue(id, out var seed))
        {
            return false;
        }

        if (seed.Provenance != CascadeLayer.Pack)
        {
            throw new InvalidOperationException(
                $"Only Pack-provenance seed types can be retracted; '{id}' is {seed.Provenance}.");
        }

        return _retractedPackSeeds.TryAdd(id, 0);
    }

    /// <inheritdoc />
    public bool RestorePackSeed(EntityTypeId id)
    {
        if (!_seeds.TryGetValue(id, out var seed))
        {
            return false;
        }

        if (seed.Provenance != CascadeLayer.Pack)
        {
            throw new InvalidOperationException(
                $"Only Pack-provenance seed types can be restored; '{id}' is {seed.Provenance}.");
        }

        return _retractedPackSeeds.TryRemove(id, out _);
    }

    /// <inheritdoc />
    public EntityTypeSeed? GetSeed(EntityTypeId id)
        => _retractedPackSeeds.ContainsKey(id) ? null : _seeds.GetValueOrDefault(id);

    /// <inheritdoc />
    public IReadOnlyList<EntityTypeSeed> ListSeeds()
        => _seeds.Where(kvp => !_retractedPackSeeds.ContainsKey(kvp.Key)).Select(kvp => kvp.Value).ToList();

    /// <inheritdoc />
    public Task<EntityType> CreateTypeAsync(
        EntityType type, Instant at, string? actorRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        RegistryTenantGuard.Require(type.TenantId);

        if (type.Provenance.IsSeed())
        {
            throw new ArgumentException(
                $"A tenant type row must be declared at a non-seed layer (Tenant/Instance); got "
                + $"{type.Provenance}. Shared templates go through SeedType.",
                nameof(type));
        }

        RequireAtLeastOneTrait(type.Descriptor, nameof(type));

        if (_typesByTenant.TryGetValue((type.TenantId, type.Id), out var existing)
            && existing.OverrideOf == type.Id
            && _retractedPackSeeds.ContainsKey(type.Id))
        {
            throw new InvalidOperationException(
                $"Tenant type '{type.Id}' is a retained override of a retracted Pack seed and cannot be overwritten.");
        }
        _typesByTenant[(type.TenantId, type.Id)] = type;
        _audit.Append(type.TenantId, type.Id.Value, RegistryOp.EntityTypeCreated, at, actorRef,
            detail: $"trait={type.Descriptor.Traits}");
        return Task.FromResult(type);
    }

    /// <inheritdoc />
    public Task<EntityType> OverrideSeedAsync(
        TenantId tenant,
        EntityTypeId seedId,
        EntityTypeDescriptor overrideDescriptor,
        Instant at,
        CascadeLayer layer = CascadeLayer.Tenant,
        string? actorRef = null,
        CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        ArgumentNullException.ThrowIfNull(overrideDescriptor);

        if (layer.IsSeed())
        {
            throw new ArgumentException(
                $"An override must be declared at a non-seed layer (Tenant/Instance); got {layer}.",
                nameof(layer));
        }

        if (!_seeds.ContainsKey(seedId) || _retractedPackSeeds.ContainsKey(seedId))
        {
            throw new InvalidOperationException($"Cannot override unknown seed type '{seedId}'.");
        }

        RequireAtLeastOneTrait(overrideDescriptor, nameof(overrideDescriptor));

        var overrideRow = new EntityType
        {
            Id = seedId,
            TenantId = tenant,
            Descriptor = overrideDescriptor,
            Provenance = layer,
            OverrideOf = seedId,
        };

        // The seed itself is untouched — this only writes a tenant-scoped row.
        _typesByTenant[(tenant, seedId)] = overrideRow;
        _audit.Append(tenant, seedId.Value, RegistryOp.EntityTypeOverridden, at, actorRef,
            detail: $"override-of={seedId}");
        return Task.FromResult(overrideRow);
    }

    /// <inheritdoc />
    public Task<bool> RevertOverrideAsync(
        TenantId tenant, EntityTypeId seedId, Instant at, string? actorRef = null,
        CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        if (!_seeds.ContainsKey(seedId))
        {
            throw new InvalidOperationException($"Cannot revert an override of unknown seed type '{seedId}'.");
        }

        // Only a tenant row that is genuinely an OVERRIDE of this seed may be reverted; a greenfield
        // type that happens to share the id is not "an override" and must not be silently discarded here.
        if (!_typesByTenant.TryGetValue((tenant, seedId), out var existing))
        {
            return Task.FromResult(false); // idempotent no-op: nothing to revert.
        }

        if (existing.OverrideOf != seedId)
        {
            throw new InvalidOperationException(
                $"Tenant type '{seedId}' is not an override of seed '{seedId}' (it is a greenfield type); "
                + "revert only removes a seed override, never a tenant's own type.");
        }

        _typesByTenant.TryRemove((tenant, seedId), out _);
        _audit.Append(tenant, seedId.Value, RegistryOp.EntityTypeOverrideReverted, at, actorRef,
            detail: $"reverted-override-of={seedId}");
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<EntityType?> GetTypeAsync(TenantId tenant, EntityTypeId id, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        _typesByTenant.TryGetValue((tenant, id), out var type);
        if (type?.OverrideOf == id && _retractedPackSeeds.ContainsKey(id))
        {
            type = null;
        }
        return Task.FromResult<EntityType?>(type);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EntityType>> ListTypesAsync(TenantId tenant, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);
        IReadOnlyList<EntityType> result = _typesByTenant
            .Where(kvp => kvp.Key.Tenant.Equals(tenant))
            .Select(kvp => kvp.Value)
            .Where(type => type.OverrideOf != type.Id || !_retractedPackSeeds.ContainsKey(type.Id))
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<FormBindingRef?> TryResolvePropertyFormAsync(
        TenantId tenant, EntityTypeId typeId, CancellationToken cancellationToken = default)
    {
        RegistryTenantGuard.Require(tenant);

        // Effective binding: tenant override if present, else the shared seed. A private type owned
        // by ANOTHER tenant is never in this tenant's map and is not a seed, so it is unreachable —
        // no bare-hash lookup exists (F2 invariant 2).
        if (_typesByTenant.TryGetValue((tenant, typeId), out var tenantType))
        {
            if (tenantType.OverrideOf == typeId && _retractedPackSeeds.ContainsKey(typeId))
            {
                return Task.FromResult<FormBindingRef?>(null);
            }
            return Task.FromResult(tenantType.Descriptor.PropertyFormBinding);
        }

        if (!_retractedPackSeeds.ContainsKey(typeId) && _seeds.TryGetValue(typeId, out var seed))
        {
            return Task.FromResult(seed.Descriptor.PropertyFormBinding);
        }

        return Task.FromResult<FormBindingRef?>(null);
    }

    private static void RequireAtLeastOneTrait(EntityTypeDescriptor descriptor, string paramName)
    {
        if (descriptor.Traits == EntityTrait.None)
        {
            throw new ArgumentException(
                "An entity type must declare at least one trait (container / maintainable / movable).",
                paramName);
        }
    }
}
