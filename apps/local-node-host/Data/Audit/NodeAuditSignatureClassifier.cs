using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Computes the offline <c>signature_state</c> (<c>Verified</c> | <c>VerificationFailed</c> |
/// <c>NotSigned</c>) for a node audit row at read time (ADR 0126 §D4 / OQ1 ruling). The exact enum
/// values <c>audit-events.ts</c> expects.
/// </summary>
/// <remarks>
/// <para>
/// <b>HashChain is the authoritative integrity signal across a reseed (OQ1).</b> The integrity verdict
/// is driven by the offline, key-independent SHA-256 hash chain (<see cref="NodeAuditHashChain"/>),
/// which needs no key material and survives a passphrase reseed. The keyed Ed25519
/// <see cref="NodeAuditEventRow.Signature"/> is secondary: it derives from the root seed, so after a
/// reseed a valid pre-reseed signature would otherwise verify against the NEW key and produce a false
/// <c>VerificationFailed</c>.
/// </para>
/// <para>
/// <b>The ruling (OQ1), applied here:</b>
/// <list type="number">
///   <item>If the row's hash does NOT recompute against its predecessor → <c>VerificationFailed</c>
///     (genuine tamper-evidence; the chain is broken).</item>
///   <item>If the chain is intact AND the row is unsigned (<c>Signature == null</c>) →
///     <c>NotSigned</c> (the node posting path is unsigned for v1; chain-intact, not signed).</item>
///   <item>If the chain is intact AND the row IS signed: verify the signature against the sealed
///     pre-reseed epoch key when the row predates a reseed boundary. <c>Verified</c> on a genuine
///     pass; degrade to <c>NotSigned</c> (the closest existing enum value — a historical /
///     unverifiable-under-a-retired-key state) when the epoch key is unavailable. <b>NEVER</b>
///     <c>VerificationFailed</c> for a retired-epoch record — that conflates "tampered" with "signed
///     under a retired key." A genuine post-reseed signature failure (current epoch) is
///     <c>VerificationFailed</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>v1 signature verification (ADR 0135 — the per-event signed event-log PASS-gate).</b> When a
/// <see cref="NodeAuditSignatureVerificationContext"/> is supplied (the current node issuer + the
/// foundation <see cref="IOperationVerifier"/>), the classifier performs REAL Ed25519 re-verification
/// of a signed row: it reconstructs the <see cref="SignedOperation{T}"/> envelope via
/// <see cref="NodeAuditSignaturePayload"/> against the appropriate issuer (the current node issuer for
/// a current-epoch row; the sealed pre-reseed epoch issuer for a retired-key row) and calls
/// <see cref="IOperationVerifier.Verify"/>. A genuine signature failure on a CURRENT-epoch row →
/// <c>VerificationFailed</c> (a tampered signed record is detected). A pre-reseed record whose sealed
/// epoch key is UNAVAILABLE degrades to <c>NotSigned</c> — NEVER <c>VerificationFailed</c> — so a
/// passphrase reseed never falsely flags retired-key history (the property OQ1.2 protects; the hash
/// chain remains the authoritative integrity signal across the reseed).
/// </para>
/// <para>
/// <b>No-context fallback (backward-compat).</b> When NO verification context is supplied (the
/// 3-argument overload — used by the SC4 guard path and any caller that has not wired the node
/// verifier), the classifier keeps the prior conservative posture: it verifies the HASH CHAIN fully and
/// treats a signed + chain-intact row as <c>Verified</c> (covering-epoch aware) WITHOUT re-checking the
/// Ed25519 signature. This cannot produce a false <c>VerificationFailed</c>. The
/// <see cref="NodeAuditEventReader"/> supplies the context, so the live read path performs real
/// verification. The epoch/key-id-in-envelope move (vs the out-of-band sealed-epoch table) remains
/// forward-watched for ADR 0004 algorithm-agility (ADR 0126 §D4 / SE-3).
/// </para>
/// </remarks>
public static class NodeAuditSignatureClassifier
{
    /// <summary>The signature-state enum values, matching <c>audit-events.ts SignatureState</c>.</summary>
    public const string Verified = "Verified";
    /// <summary>Genuine tamper-evidence: the hash chain does not recompute.</summary>
    public const string VerificationFailed = "VerificationFailed";
    /// <summary>Unsigned, or signed under a retired epoch whose key is unavailable (historical).</summary>
    public const string NotSigned = "NotSigned";

    /// <summary>
    /// Classifies one row given its predecessor's hash and the sealed signature epochs (ordered by
    /// boundary ascending), WITHOUT re-checking the Ed25519 signature — the conservative chain-only
    /// posture (no verification context). <paramref name="prevHash"/> must be the hash of the
    /// immediately-preceding row in this tenant's chain (null for the first row).
    /// </summary>
    /// <remarks>
    /// Backward-compat overload for callers that have not wired the node verifier (e.g. the SC4 guard
    /// path). The live <see cref="NodeAuditEventReader"/> uses the
    /// <see cref="Classify(NodeAuditEventRow, string?, IReadOnlyList{NodeAuditSignatureEpochRow}, NodeAuditSignatureVerificationContext?)"/>
    /// overload with a context, performing real Ed25519 re-verification (ADR 0135 PASS-gate).
    /// </remarks>
    public static string Classify(
        NodeAuditEventRow row,
        string? prevHash,
        IReadOnlyList<NodeAuditSignatureEpochRow> epochs)
        => Classify(row, prevHash, epochs, verification: null);

    /// <summary>
    /// Classifies one row given its predecessor's hash, the sealed signature epochs (ordered by boundary
    /// ascending), and an optional <paramref name="verification"/> context. When the context is
    /// supplied AND the row is signed, performs REAL Ed25519 re-verification (ADR 0135 — the per-event
    /// signed event-log PASS-gate). <paramref name="prevHash"/> must be the hash of the
    /// immediately-preceding row in this tenant's chain (null for the first row).
    /// </summary>
    public static string Classify(
        NodeAuditEventRow row,
        string? prevHash,
        IReadOnlyList<NodeAuditSignatureEpochRow> epochs,
        NodeAuditSignatureVerificationContext? verification)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(epochs);

        // 1. Integrity FIRST — the authoritative signal across a reseed (OQ1). The key-independent hash
        // chain is what catches PAYLOAD tampering (a mutated payload/hash) regardless of signature.
        if (!NodeAuditHashChain.VerifyRow(row, prevHash))
        {
            return VerificationFailed;
        }

        // 2. Chain intact + unsigned → NotSigned (the v0 historical / unsigned state). A null signature
        // is always valid by the hash chain alone — backward-compat for pre-PASS-gate rows.
        if (row.Signature is null || row.Signature.Length == 0)
        {
            return NotSigned;
        }

        // 3. Chain intact + signed. Resolve whether this is a current-epoch or a pre-reseed (retired-key)
        // record, then verify the Ed25519 signature against the matching issuer/key.
        var latestBoundary = epochs.Count == 0
            ? (DateTimeOffset?)null
            : epochs.Max(e => e.BoundaryAt);
        var isPreReseed = latestBoundary is { } boundary && row.OccurredAt < boundary;

        if (isPreReseed)
        {
            // Pre-reseed signed record. The covering sealed epoch (the earliest boundary strictly after
            // the record's OccurredAt) holds the retired verifying key. When NO covering epoch exists,
            // the retired key is unavailable → degrade to NotSigned (historical) — NEVER
            // VerificationFailed (OQ1.2: a reseed must not falsely flag retired-key history).
            var coveringEpoch = epochs
                .Where(e => e.BoundaryAt > row.OccurredAt)
                .OrderBy(e => e.BoundaryAt)
                .FirstOrDefault();
            if (coveringEpoch is null)
            {
                return NotSigned;
            }

            // No verification context (conservative fallback) → trust chain + covering epoch.
            if (verification is null)
            {
                return Verified;
            }

            // Real verification against the SEALED OLD key. A pre-reseed signature that does not verify
            // under its retired epoch key is NOT treated as current tamper-evidence (the key is retired
            // and the chain is authoritative across the reseed) → degrade to NotSigned, not
            // VerificationFailed (OQ1.2).
            PrincipalId epochIssuer;
            try
            {
                epochIssuer = PrincipalId.FromBytes(coveringEpoch.SealedPublicKey);
            }
            catch (ArgumentException)
            {
                return NotSigned; // malformed sealed key — unverifiable-under-retired-key, not tamper.
            }
            return VerifySignature(row, epochIssuer, verification.Verifier)
                ? Verified
                : NotSigned;
        }

        // Current-epoch (or no-reseed) signed record, chain intact.
        // No verification context (conservative fallback) → trust chain.
        if (verification is null)
        {
            return Verified;
        }

        // Real verification against the CURRENT node issuer. A current-epoch signed row whose signature
        // does not verify is genuine tamper-evidence → VerificationFailed.
        return VerifySignature(row, verification.CurrentIssuerId, verification.Verifier)
            ? Verified
            : VerificationFailed;
    }

    /// <summary>
    /// Re-verifies the row's stored signature by reconstructing the <see cref="SignedOperation{T}"/>
    /// envelope (<see cref="NodeAuditSignaturePayload"/>) against <paramref name="issuerId"/> and calling
    /// the foundation verifier. Returns <c>false</c> when the row's signature/audit-id cannot form a
    /// valid envelope (treated as not-verifiable, never thrown).
    /// </summary>
    private static bool VerifySignature(
        NodeAuditEventRow row,
        PrincipalId issuerId,
        IOperationVerifier verifier)
    {
        var op = NodeAuditSignaturePayload.TryReconstruct(row, issuerId, row.Signature);
        return op is not null && verifier.Verify(op);
    }

    /// <summary>
    /// Loads the sealed signature epochs for a tenant from the recoverable store (ordered by boundary
    /// ascending). The epoch table is small (one row per reseed); a full read is cheap.
    /// </summary>
    public static async Task<IReadOnlyList<NodeAuditSignatureEpochRow>> LoadEpochsAsync(
        LocalNodeDbContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return await ctx.Set<NodeAuditSignatureEpochRow>()
            .OrderBy(e => e.BoundaryAt)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
