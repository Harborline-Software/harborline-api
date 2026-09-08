using Harborline.Api.Foundation.MultiTenancy;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// A first-class, typed, <b>dated</b> relationship between two registry entities (annex §3.1) —
/// containment, location-over-time, or system membership. Hierarchy and location history are
/// VIEWS over these edges; there is never a stored parent pointer.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IMustHaveTenant"/>. Both endpoints and the edge share ONE tenant — the
/// edge store forbids cross-tenant edges and rejects the system / default <see cref="TenantId"/>
/// sentinel (ADR 0101 Rev 3.1 / A5a).
/// </para>
/// <para>
/// The edge is effective over <c>[<see cref="EffectiveFrom"/>, <see cref="EffectiveTo"/>)</c>; a
/// null <see cref="EffectiveTo"/> means open-ended (currently effective). "Where is it / what
/// contains it" queries always take an explicit as-of clock and select the edge effective at that
/// instant — never ambient <c>DateTime.Now</c> (A5c).
/// </para>
/// </remarks>
public sealed record TypedRelationship : IMustHaveTenant
{
    /// <summary>Stable id for this edge.</summary>
    public required TypedRelationshipId Id { get; init; }

    /// <summary>Owning tenant (shared by both endpoints). Required; sentinel rejected by the store.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The relationship kind.</summary>
    public required RelationshipKind Kind { get; init; }

    /// <summary>
    /// The source endpoint. For <see cref="RelationshipKind.Contains"/> this is the container; for
    /// <see cref="RelationshipKind.LocatedAt"/> / <see cref="RelationshipKind.PartOfSystem"/> it is
    /// the subject entity.
    /// </summary>
    public required RegistryEntityId From { get; init; }

    /// <summary>
    /// The target endpoint. For <see cref="RelationshipKind.Contains"/> this is the child; for
    /// <see cref="RelationshipKind.LocatedAt"/> the place; for
    /// <see cref="RelationshipKind.PartOfSystem"/> the system.
    /// </summary>
    public required RegistryEntityId To { get; init; }

    /// <summary>Instant the edge became effective.</summary>
    public required Instant EffectiveFrom { get; init; }

    /// <summary>Instant the edge ceased to be effective; null = open-ended / currently effective.</summary>
    public Instant? EffectiveTo { get; init; }

    /// <summary>True when this edge is effective at <paramref name="asOf"/> (half-open interval).</summary>
    public bool IsEffectiveAt(Instant asOf) =>
        EffectiveFrom.Value <= asOf.Value && (EffectiveTo is null || asOf.Value < EffectiveTo.Value.Value);
}
