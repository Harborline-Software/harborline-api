using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Audit;

/// <summary>
/// One append-only, hash-chained audit record of a registry mutation. The chain is per
/// <c>(tenant, subject)</c>: each event links to the hash of the previous event on the same
/// subject, so tampering is detectable via <see cref="IRegistryAuditLog.VerifyChain"/>
/// (mirrors the foundation <c>IAuditLog</c> hash-chain discipline).
/// </summary>
/// <param name="Sequence">Monotonic global append sequence (assigned by the log).</param>
/// <param name="Tenant">The owning tenant.</param>
/// <param name="Subject">
/// The mutated row's id as an opaque string (an <c>EntityTypeId</c>, <c>RegistryEntityId</c>,
/// <c>TypedRelationshipId</c>, or <c>ConditionAssessmentId</c>). Generic so one journal spans all
/// registry aggregates.
/// </param>
/// <param name="Op">The mutation kind.</param>
/// <param name="At">The instant the mutation was recorded (supplied by the caller; as-of clock).</param>
/// <param name="ActorRef">Opaque reference to the actor responsible, if known.</param>
/// <param name="Detail">Optional short human/JSON detail of the change (no PII values).</param>
/// <param name="PreviousHash">Hex hash of the previous event on this subject's chain; null for the first.</param>
/// <param name="Hash">Hex SHA-256 hash of this event's canonical form (assigned by the log).</param>
public sealed record RegistryAuditEvent(
    long Sequence,
    TenantId Tenant,
    string Subject,
    RegistryOp Op,
    Instant At,
    string? ActorRef,
    string? Detail,
    string? PreviousHash,
    string Hash);
