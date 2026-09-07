namespace Harborline.Api.Blocks.Assets.Registry.Audit;

/// <summary>
/// The mutation kinds recorded on the registry's append-only audit journal. Every mutating
/// repository operation emits exactly one event of one of these kinds (ADR 0101 Rev 3.1 Wave 1
/// constraint: "all mutations ride the X-AUDIT durable layer").
/// </summary>
public enum RegistryOp
{
    /// <summary>A tenant-scoped entity type row was created.</summary>
    EntityTypeCreated = 0,

    /// <summary>A tenant produced an override row of a shared seed template (F2 invariant 1).</summary>
    EntityTypeOverridden = 1,

    /// <summary>A typed entity instance was created.</summary>
    EntityCreated = 2,

    /// <summary>A typed entity instance was updated.</summary>
    EntityUpdated = 3,

    /// <summary>A typed entity instance was retired (soft-delete).</summary>
    EntityRetired = 4,

    /// <summary>A typed relationship (edge) was added.</summary>
    RelationshipAdded = 5,

    /// <summary>A typed relationship (edge) was closed (its <c>EffectiveTo</c> was set).</summary>
    RelationshipClosed = 6,

    /// <summary>A condition assessment was recorded.</summary>
    ConditionAssessed = 7,

    /// <summary>
    /// A binding-declared condition-capture projection was SKIPPED for a diagnosable reason (an
    /// out-of-range grade, or a declared target that resolved cross-tenant / dangling / unresolvable) —
    /// ADR 0101 Rev 3.1 Wave 2b / F-SKIP. This is the durable trace an operator reads to learn that an
    /// INTENDED capture produced no record; a genuine no-op (no binding, an unfilled optional field) is
    /// NOT recorded, so a skip event always means "something the tenant configured did not land."
    /// </summary>
    ProjectionSkipped = 8,

    /// <summary>
    /// A tenant's override row of a shared seed was REVERTED — the tenant-scoped override is removed so
    /// the shared seed template is once again this tenant's effective type (the inverse of
    /// <see cref="EntityTypeOverridden"/>). The seed itself is never touched (it was immutable
    /// throughout, F2 invariant 1); only the tenant's own override row is discarded. ADR 0101 Rev 3.1
    /// / Type Manager (Wave 2c).
    /// </summary>
    EntityTypeOverrideReverted = 9,

    /// <summary>
    /// A form submission was linked to a record — the generic "this submission was filled into this
    /// entity" side record the runtime form-fill surface writes (#144). Emitted when a submission carries
    /// a case ref that resolves to an entity under the tenant, so the record's detail panel can list its
    /// submitted forms. Like <see cref="ConditionAssessed"/> it rides the durable X-AUDIT chain; unlike a
    /// condition capture it carries no grade — only the (entity, instance, form) linkage + the submit clock.
    /// </summary>
    FormSubmissionRecorded = 10,

    /// <summary>
    /// A spatial-frame descriptor epoch was MINTED (ADR 0101 Rev 3.2 Wave 5 / [A11]; ADR 0168
    /// D2-A3/D2-A4). Maps explicitly to the canonical <c>Op.Mint</c> — never the write fallthrough.
    /// The audit <c>Detail</c> carries ONLY the identity triple (anchor, frameCode, frameEpoch) —
    /// never <c>originDescription</c>, never georeference ordinates.
    /// </summary>
    SpatialFrameDescriptorMinted = 11,

    /// <summary>
    /// A spatial-frame mint LOST and its content was quarantined (ADR 0168 D2-A6). Maps explicitly
    /// to the additive canonical <c>Op.Reject</c> — a rejected mint recorded as a non-destructive
    /// update (<c>Op.Write</c>) would be a misclassification. <c>Detail</c> is the identity triple
    /// only; the losing content lives in the sealed quarantine row, referenced — never inlined —
    /// because the hash-chained journal has no redaction path ([A11]).
    /// </summary>
    SpatialFrameDescriptorConflictDetected = 12,

    /// <summary>
    /// A spatial-frame read UNSEALED the two governed PII cells (<c>originDescription</c>,
    /// <c>georeference</c>) — the <c>pii</c> binding's <c>Audit@Read</c> effect, enforced
    /// STORE-LEVEL at the descriptor unsealing seam (CIC ruling 2026-08-06). One event per
    /// unsealed row (descriptor or quarantine), bounded to ACTUAL unsealing — a redacted read
    /// emits nothing. Maps explicitly to the canonical <c>Op.Read</c> (the <c>Op.Reject</c>
    /// additive precedent) — NEVER the <c>Op.Write</c> fallthrough. <c>Detail</c> carries ONLY
    /// the identity triple — never the unsealed values. The append is fatal on failure: a read
    /// that cannot be audited is not surfaced.
    /// </summary>
    SpatialFrameDescriptorPiiUnsealed = 13,
}
