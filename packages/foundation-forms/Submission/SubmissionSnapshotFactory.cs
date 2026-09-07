using System.Security.Cryptography;
using System.Text.Json;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// Builds the conditional <see cref="SubmissionSnapshot"/> artifact for a submission
/// once <see cref="SubmissionSnapshotGate"/> has decided to capture (ADR 0140 D3).
/// </summary>
/// <remarks>
/// Two shapes, matching the two capture modes:
/// <list type="bullet">
///   <item><description><see cref="FullProjection"/> — stores the full as-shown
///     projection document. To avoid doubling PII on the edge tier the caller passes
///     the at-rest (encrypted) body, not a fresh cleartext copy.</description></item>
///   <item><description><see cref="SignedAsync"/> — computes a digest over the
///     canonical DTBS bytes, signs the digest, and stores ONLY the hash + signature.
///     No cleartext projection is retained — the GDPR-minimal path.</description></item>
/// </list>
/// </remarks>
public static class SubmissionSnapshotFactory
{
    /// <summary>The digest algorithm used over the DTBS for the signed mode.</summary>
    public const string HashAlgorithm = "SHA-256";

    /// <summary>
    /// Captures a full as-shown projection snapshot (compliance-grade path). Takes
    /// ownership of a clone of <paramref name="asShownProjection"/> so the snapshot is
    /// stable against disposal of the source document.
    /// </summary>
    /// <param name="asShownProjection">The as-shown projection to retain. Pass the
    /// at-rest (encrypted) body to keep PII from being doubled in cleartext.</param>
    /// <param name="capturedAt">The UTC capture instant.</param>
    public static SubmissionSnapshot FullProjection(JsonDocument asShownProjection, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(asShownProjection);
        // Clone so the snapshot outlives the caller's document lifetime.
        var retained = JsonDocument.Parse(asShownProjection.RootElement.GetRawText());
        return new SubmissionSnapshot(SnapshotCaptureMode.FullProjection, capturedAt, FullProjection: retained);
    }

    /// <summary>
    /// Captures a signed DTBS-hash snapshot (signed path): hashes the canonical DTBS
    /// bytes, signs the hash via <paramref name="signer"/>, and retains only
    /// {hash, signature} — no cleartext projection.
    /// </summary>
    /// <param name="dtbs">The canonical data-to-be-signed bytes (the as-shown rendering).</param>
    /// <param name="signer">The signing seam.</param>
    /// <param name="capturedAt">The UTC capture instant.</param>
    /// <param name="ct">Cancellation.</param>
    public static async ValueTask<SubmissionSnapshot> SignedAsync(
        ReadOnlyMemory<byte> dtbs,
        ISubmissionSigner signer,
        DateTimeOffset capturedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var hash = SHA256.HashData(dtbs.Span);
        var signature = await signer.SignAsync(hash, ct).ConfigureAwait(false);

        var signed = new SignedDtbsHash(
            HashAlgorithm: HashAlgorithm,
            DtbsHash: hash,
            SignatureAlgorithm: signer.SignatureAlgorithm,
            Signature: signature,
            PublicKeyRef: signer.PublicKeyRef);

        return new SubmissionSnapshot(SnapshotCaptureMode.SignedDtbsHash, capturedAt, Signed: signed);
    }
}
