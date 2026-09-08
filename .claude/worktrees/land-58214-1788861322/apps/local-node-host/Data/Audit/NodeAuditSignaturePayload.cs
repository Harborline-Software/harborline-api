using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// The single source of truth for the <see cref="SignedOperation{T}"/> envelope a node audit row's
/// per-event signature covers (ADR 0135 — per-event signed event-log, the binding pre-multi-device
/// PASS-gate; SEC-A2 of the 0135 council). The WRITE side (<see cref="NodeAuditWriteEnlister"/>) and
/// the READ side (<see cref="NodeAuditSignatureClassifier"/>) BOTH reconstruct the envelope here so
/// they agree byte-for-byte — a signature produced at write time re-verifies at read time iff the same
/// fields are fed in.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is signed (v1, ADR 0049 format-gate).</b> The envelope is
/// <c>SignedOperation&lt;string&gt;</c> over the row's exact persisted canonical-JSON
/// <see cref="NodeAuditEventRow.Payload"/> (the <c>T = string</c> payload), with:
/// <list type="bullet">
///   <item><c>IssuedAt</c> = the row's <see cref="NodeAuditEventRow.OccurredAt"/> (already persisted,
///     round-trip-stable through the ISO-8601-UTC value-converter).</item>
///   <item><c>Nonce</c> = <c>Guid.Parse(row.AuditId)</c> — the row's stable GUID-D identifier, ALREADY
///     persisted, so no new column is needed AND the nonce is reproducible at read time. (The audit
///     id is unique per record; reusing it as the per-issuance nonce is sound — the higher-layer
///     replay concern the nonce normally addresses does not apply to an append-only, single-writer,
///     local audit log.)</item>
///   <item><c>IssuerId</c> = the signing node's principal id (the node's <c>RootSeedHex</c>→Ed25519
///     identity, ADR 0118 custody ladder). Supplied by the caller — at write time it is the live
///     <c>IOperationSigner.IssuerId</c>; at read time it is the current node issuer (or the sealed
///     pre-reseed epoch issuer for a retired-key record).</item>
/// </list>
/// The <see cref="Harborline.Api.Foundation.Crypto.Ed25519Signer"/> signs the canonical-JSON form of
/// <c>{issuedAt, issuerId, nonce, payload}</c>; <see cref="Harborline.Api.Foundation.Crypto.Ed25519Verifier"/> verifies
/// the same canonical form against the public key embedded in <c>IssuerId</c>. Nothing here invents
/// crypto — it only assembles the envelope the audited foundation signer/verifier already own.
/// </para>
/// <para>
/// <b>Format version.</b> A row with a non-null <see cref="NodeAuditEventRow.Signature"/> is a v1
/// (signed) record; a null signature is a v0 (historical / unsigned) record. The gate is the
/// presence of the signature, not a separate column — the reader branches on it
/// (<see cref="NodeAuditSignatureClassifier"/>), and v0 rows remain valid by the hash chain alone.
/// </para>
/// </remarks>
public static class NodeAuditSignaturePayload
{
    /// <summary>
    /// Reconstructs the <see cref="SignedOperation{T}"/> envelope for a stored row given the
    /// <paramref name="issuerId"/> the signature was (or is to be) attributed to and the raw 64-byte
    /// <paramref name="signatureBytes"/>. Used by the READ side to re-verify.
    /// </summary>
    /// <param name="row">The persisted audit row.</param>
    /// <param name="issuerId">The signing principal id (current node issuer or a sealed-epoch issuer).</param>
    /// <param name="signatureBytes">The row's raw Ed25519 signature bytes (exactly 64 bytes).</param>
    /// <returns>The reconstructed signed envelope, or <c>null</c> when the row's
    /// <see cref="NodeAuditEventRow.AuditId"/> is not a parseable GUID (the nonce cannot be recovered —
    /// a malformed/legacy id; the caller treats that as not-verifiable rather than a hard failure).</returns>
    public static SignedOperation<string>? TryReconstruct(
        NodeAuditEventRow row,
        PrincipalId issuerId,
        ReadOnlySpan<byte> signatureBytes)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (signatureBytes.Length != Signature.LengthInBytes)
        {
            return null;
        }
        if (!Guid.TryParse(row.AuditId, out var nonce))
        {
            return null;
        }

        return new SignedOperation<string>(
            Payload: row.Payload,
            IssuerId: issuerId,
            IssuedAt: row.OccurredAt,
            Nonce: nonce,
            Signature: Signature.FromBytes(signatureBytes));
    }

    /// <summary>
    /// The deterministic per-issuance nonce for a row — its <see cref="NodeAuditEventRow.AuditId"/>
    /// parsed as a GUID. Used by the WRITE side so the value it signs over matches what
    /// <see cref="TryReconstruct"/> recovers at read time.
    /// </summary>
    public static Guid NonceFor(string auditId)
    {
        ArgumentException.ThrowIfNullOrEmpty(auditId);
        return Guid.Parse(auditId);
    }
}

/// <summary>
/// The read-side context that lets <see cref="NodeAuditSignatureClassifier"/> perform REAL Ed25519
/// re-verification of a signed node audit row (ADR 0135 — the per-event signed event-log PASS-gate):
/// the CURRENT node issuer (the <c>RootSeedHex</c>→Ed25519 identity a current-epoch signature is
/// attributed to) plus the foundation <see cref="IOperationVerifier"/>. Pre-reseed (retired-key) rows
/// are verified against the sealed-epoch issuer instead; the classifier resolves which to use.
/// </summary>
/// <param name="CurrentIssuerId">The current node signing identity's principal id.</param>
/// <param name="Verifier">The foundation Ed25519 verifier (stateless; shareable).</param>
public sealed record NodeAuditSignatureVerificationContext(
    PrincipalId CurrentIssuerId,
    IOperationVerifier Verifier);
