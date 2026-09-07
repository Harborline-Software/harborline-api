using Harborline.Api.Foundation.MultiTenancy;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// A typed entity instance in the registry (annex §3.1) — the single generic entity model that
/// replaces the Rev-2 places/assets split. A bedroom, a water heater, a window, a carpet piece,
/// and a whole property are all <see cref="RegistryEntity"/> rows distinguished only by their
/// <see cref="Type"/>'s traits.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IMustHaveTenant"/>; the repository rejects the system / default
/// <see cref="TenantId"/> sentinel. There is <b>no stored parent pointer</b> — spatial position
/// and location are typed dated <see cref="TypedRelationship"/> edges; hierarchy is a VIEW over
/// those edges (ADR 0101 Rev 3.1).
/// </para>
/// <para>
/// <see cref="PropertyForm"/> is this entity's own pinned reference to its type's property form:
/// its captured values were authored under exactly that <see cref="FormBindingRef.PinnedVersion"/>,
/// so a later revision of the type's property form does not invalidate them (D-D).
/// </para>
/// </remarks>
public sealed record RegistryEntity : IMustHaveTenant
{
    /// <summary>Stable, generic entity id — the ref condition + scoring records key off (A4).</summary>
    public required RegistryEntityId Id { get; init; }

    /// <summary>Owning tenant. Required (system / default sentinel rejected by the repository).</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The entity's type (its traits, property form, inspection bindings).</summary>
    public required EntityTypeId Type { get; init; }

    /// <summary>Human-friendly name (e.g. "Master-bath water heater", "Bedroom 2").</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// This entity's pinned property-form reference (content-addressed id + the exact version its
    /// values were authored against). Null when the type declares no property form.
    /// </summary>
    public FormBindingRef? PropertyForm { get; init; }

    /// <summary>
    /// Optional scan/search key (QR / barcode / serial) — field entry is scan-first (annex §3.1).
    /// Opaque string first-slice.
    /// </summary>
    public string? ScanKey { get; init; }

    /// <summary>Record-creation instant; immutable after first persist.</summary>
    public required Instant CreatedAt { get; init; }

    /// <summary>
    /// Retirement instant (soft-delete marker). Records remain queryable with
    /// <c>includeRetired: true</c> but are excluded from default listings.
    /// </summary>
    public Instant? RetiredAt { get; init; }
}
