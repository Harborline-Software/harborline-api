using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// An <b>immutable shared type template</b> shipped by the platform base or a pack (annex §3.2;
/// ADR 0101 Rev 3.1 / security-engineering finding <b>F2, invariant 1</b>).
/// </summary>
/// <remarks>
/// <para>
/// A seed is NOT tenant-scoped — it is shared, read-only catalog config visible to every tenant.
/// A tenant that "extends/overrides" a seed <b>never mutates it</b>; the registry produces a new
/// tenant-scoped <see cref="EntityType"/> override row instead (enforced structurally: a seed and
/// an override are different types, and the seed collection is append-only + never handed out as
/// a mutable reference). This is what keeps one tenant's tuning from leaking into another's view.
/// </para>
/// <para>
/// <see cref="Provenance"/> is always a seed layer (<see cref="CascadeLayer.Base"/> or
/// <see cref="CascadeLayer.Pack"/>); the registry rejects a seed declared at a tenant/instance
/// layer.
/// </para>
/// </remarks>
/// <param name="Id">Stable type id (shared across tenants).</param>
/// <param name="Descriptor">The type's descriptive payload.</param>
/// <param name="Provenance">The seed layer — <see cref="CascadeLayer.Base"/> or <see cref="CascadeLayer.Pack"/>.</param>
public sealed record EntityTypeSeed(
    EntityTypeId Id,
    EntityTypeDescriptor Descriptor,
    CascadeLayer Provenance);
