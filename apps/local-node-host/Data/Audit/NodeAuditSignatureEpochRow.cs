namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// A sealed pre-reseed signature epoch (ADR 0126 §D4 / OQ1 ruling). At passphrase-reseed time the
/// OLD public key is sealed as an epoch so pre-reseed audit signatures can still be verified against
/// the retired key after the reseed mints a new signing identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The keyed Ed25519 audit <c>signature</c> derives from the root seed.
/// <c>IOperationVerifier.Verify&lt;T&gt;</c> resolves the verifying key from the issuer id, so after a
/// reseed the issuer maps to the NEW key and a valid pre-reseed signature would return
/// <c>VerificationFailed</c> — a false alarm. Sealing the OLD public key here lets the reader verify
/// pre-reseed records (those whose <c>OccurredAt</c> precedes <see cref="BoundaryAt"/>) against the
/// retired key; <c>HashChain</c> remains the authoritative integrity signal across the reseed.
/// </para>
/// <para>
/// <b>Out-of-band (no envelope change for v1).</b> Per OQ1.3 the epoch boundary is recorded here, in
/// a sealed-epoch table keyed by reseed timestamp, rather than touching the
/// <c>SignedOperation&lt;T&gt;</c> envelope. When ADR 0004 (algorithm-agility) lands, the epoch/key-id
/// is promoted INTO the envelope (forward-compat, tracked — not a v1 build item).
/// </para>
/// <para>
/// <b>Recoverable.</b> Lives in <c>local-node.db</c> (Store-DEK-enveloped) so the epoch survives the
/// very reseed it describes — the retired key is available offline to verify history. When the epoch
/// key is unavailable (a reseed that destroyed the old key without sealing, or pre-cutover records),
/// the reader degrades to a historical / <c>NotSigned</c> state, NEVER <c>VerificationFailed</c>.
/// </para>
/// </remarks>
public sealed class NodeAuditSignatureEpochRow
{
    /// <summary>Monotonic epoch id (1 = first sealed epoch). Newer epochs have larger ids.</summary>
    public required long EpochId { get; init; }

    /// <summary>The reseed boundary timestamp. Records whose <c>OccurredAt</c> is strictly before
    /// this instant were signed under <see cref="SealedPublicKey"/>.</summary>
    public required DateTimeOffset BoundaryAt { get; init; }

    /// <summary>The issuer id the sealed key was registered under, so the reader can match a
    /// pre-reseed record's issuer to the retired verifying key.</summary>
    public required string IssuerId { get; init; }

    /// <summary>The sealed OLD public key bytes (the retired verifying key).</summary>
    public required byte[] SealedPublicKey { get; init; }
}
