using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// A <b>tenant-scoped</b> entity-type registry row (annex §3.2, D-G) — either a tenant's own new
/// type or its override/extension of a shared <see cref="EntityTypeSeed"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IMustHaveTenant"/>; the registry rejects rows carrying the system /
/// default <see cref="TenantId"/> sentinel (fail-closed multi-tenant isolation). Seeds are the
/// separate immutable <see cref="EntityTypeSeed"/>; a tenant edit of a seed materialises a new
/// <see cref="EntityType"/> with <see cref="OverrideOf"/> set — never a seed mutation (F2
/// invariant 1).
/// </para>
/// <para>
/// <see cref="Provenance"/> is always a non-seed layer (<see cref="CascadeLayer.Tenant"/> or
/// <see cref="CascadeLayer.Instance"/>).
/// </para>
/// </remarks>
public sealed record EntityType : IMustHaveTenant
{
    /// <summary>Stable id for this type row.</summary>
    public required EntityTypeId Id { get; init; }

    /// <summary>Owning tenant. Required (system / default sentinel rejected by the registry).</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The type's descriptive payload.</summary>
    public required EntityTypeDescriptor Descriptor { get; init; }

    /// <summary>
    /// The cascade layer this row was declared at — <see cref="CascadeLayer.Tenant"/> or
    /// <see cref="CascadeLayer.Instance"/> (never a seed layer).
    /// </summary>
    public required CascadeLayer Provenance { get; init; }

    /// <summary>
    /// When this row overrides a shared <see cref="EntityTypeSeed"/>, the seed's id; null for a
    /// tenant's own greenfield type.
    /// </summary>
    public EntityTypeId? OverrideOf { get; init; }
}
