namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// Outcome of a <see cref="ISubjectErasureService.EraseAsync"/> call.
/// </summary>
/// <param name="Outcome">Discriminator for what happened.</param>
/// <param name="Tombstone">The tombstone written (when <see cref="Outcome"/> is <see cref="SubjectErasureOutcome.Erased"/> or <see cref="SubjectErasureOutcome.AlreadyErased"/>), else <c>null</c>.</param>
public sealed record SubjectErasureResult(
    SubjectErasureOutcome Outcome,
    SubjectTombstone? Tombstone);

/// <summary>The discrete outcomes of an erasure attempt.</summary>
public enum SubjectErasureOutcome
{
    /// <summary>The subject was crypto-shredded by this call; key destroyed, tombstone written, audit emitted.</summary>
    Erased,

    /// <summary>The subject was already erased; this call was a no-op (idempotent). The existing tombstone is returned.</summary>
    AlreadyErased,

    /// <summary>
    /// The subject is under an active LEGAL HOLD (ADR 0142) — the fail-closed
    /// pre-shred gate refused. NO key was destroyed, NO tombstone was written; the
    /// attempt + block is audited (<see cref="Harborline.Api.Kernel.Audit.AuditEventType.SubjectShredBlockedByLegalHold"/>).
    /// "Hold-wins ABOVE floor-wins" (ADR 0137 §M-4): a hold overrides even a passed
    /// retention floor and a satisfied approval chain. A held subject may be erased
    /// only after an authorized ≥2-approver hold release.
    /// </summary>
    BlockedByLegalHold,
}
