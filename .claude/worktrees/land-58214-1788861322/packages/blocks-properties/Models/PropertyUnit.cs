using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Lifecycle;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Properties.Models;

/// <summary>
/// A rentable or manageable unit within a <see cref="Property"/>.
/// Single-family homes typically have one unit; multi-family may have many.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unit identity:</b> <see cref="Id"/> is an <see cref="EntityId"/> with
/// scheme <c>"unit"</c> — e.g. <c>unit:acme-rentals/550e8400-e29b</c>.
/// This matches the <c>EntityId UnitId</c> FK type already used by
/// <c>blocks-leases</c> and <c>blocks-inspections</c>, so PropertyUnit→Lease
/// / PropertyUnit→Inspection joins are O(1) by id.
/// </para>
/// <para>
/// Implements <see cref="IMustHaveTenant"/>; persistence adapters reject
/// records with the default <see cref="TenantId"/>.
/// </para>
/// </remarks>
public sealed record PropertyUnit : IMustHaveTenant, IArchivable
{
    /// <summary>
    /// Stable identifier. Scheme = <c>"unit"</c>. Use <see cref="NewId"/> to
    /// generate.
    /// </summary>
    public required EntityId Id { get; init; }

    /// <summary>Owning tenant.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>Parent property.</summary>
    public required PropertyId PropertyId { get; init; }

    /// <summary>
    /// Human-readable unit identifier within the property
    /// (e.g. <c>"101"</c>, <c>"2B"</c>, <c>"Main"</c>, <c>"Garage"</c>).
    /// </summary>
    public required string UnitNumber { get; init; }

    /// <summary>Bedroom count. Optional; null for non-residential or unknown.</summary>
    public int? Bedrooms { get; init; }

    /// <summary>Bathroom count (allows half-baths e.g. <c>1.5</c>).</summary>
    public decimal? Bathrooms { get; init; }

    /// <summary>Interior square footage. Optional.</summary>
    public decimal? SquareFootage { get; init; }

    /// <summary>Current operational status.</summary>
    public required UnitStatus Status { get; init; }

    /// <summary>Record-creation timestamp; immutable after first persist.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Archive timestamp. <c>null</c> ⇒ active; non-null ⇒ archived (recoverable)
    /// and excluded from default listings. This lifecycle field is independent of
    /// the operational <see cref="Status"/>: a unit may be archived in any status.
    /// Satisfies the ADR-0108 <see cref="IArchivable"/> contract directly: PropertyUnit
    /// stores a real <see cref="DateTimeOffset"/> timestamp, so the marker lives on the
    /// domain record itself (per-entity marker-placement ruling), not on a boundary adapter.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>Free-text notes.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Generates a new <see cref="EntityId"/> with scheme <c>"unit"</c>
    /// for use as a <see cref="PropertyUnit"/> identifier. Authority is the
    /// tenant value so unit ids are tenant-scoped at the URI level.
    /// </summary>
    public static EntityId NewId(TenantId tenant)
        => EntityId.Parse($"unit:{tenant.Value}/{Guid.NewGuid():N}");
}
