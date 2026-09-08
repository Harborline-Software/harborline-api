namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The durable EF projection of a <see cref="Harborline.Api.Foundation.Recovery.Erasure.SubjectTombstone"/> (ADR 0135
/// GDPR direction; the #1378 M-1 durable-erasure-store fix). The pseudonymized, append-only compliance record
/// written once at erasure in place of the subject's identifying fields (ADR 0068 §1.3). Co-located in the SAME
/// SQLCipher <c>local-node.db</c> file as the erasure registry it accompanies, so the tombstone survives restart.
/// </summary>
/// <remarks>
/// The <see cref="Pseudonym"/> is a deterministic, non-reversible label (NOT derived from any erased plaintext);
/// the composite <c>(TenantId, Pseudonym)</c> is the lookup + idempotency key. The approving-actor chain (ADR 0068
/// §3 floor) and the deployer-supplied legal-basis reference are persisted for the compliance record. Carries NO
/// identifying plaintext — the subject's ciphertext is already undecryptable (its sub-key is shredded).
/// </remarks>
public sealed class SubjectTombstoneRow
{
    /// <summary>The tenant the erased subject belonged to (PK with <see cref="Pseudonym"/>).</summary>
    public required string TenantId { get; set; }

    /// <summary>The non-reversible pseudonymous label that replaces the subject's identifying fields (PK with <see cref="TenantId"/>).</summary>
    public required string Pseudonym { get; set; }

    /// <summary>When the erasure took effect, Unix-ms UTC.</summary>
    public required long ErasedAtUnixMs { get; set; }

    /// <summary>The multi-actor approval chain (ADR 0068 §3 floor) as a JSON array of actor-id strings — never empty for a real erasure.</summary>
    public required string ApprovingActorsJson { get; set; }

    /// <summary>The deployer-recorded legal-determination reference (opaque to the platform per ADR 0068 §GC.1).</summary>
    public required string LegalBasis { get; set; }
}
