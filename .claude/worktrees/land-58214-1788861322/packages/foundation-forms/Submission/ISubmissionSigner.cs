namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The signing seam a signed submission uses to produce the
/// <see cref="SignedDtbsHash"/> artifact (ADR 0140 D3). Kept as a forms-local
/// abstraction so this keystone stays free of a crypto / kernel-security dependency;
/// the composition root adapts the fleet signer (e.g. <c>IBoundEd25519Signer</c> in
/// <c>kernel-security</c>) to this interface.
/// </summary>
/// <remarks>
/// The signer signs the <em>hash</em> of the rendered data-to-be-signed, never the
/// full projection — the GDPR-minimal path D3 prefers.
/// </remarks>
public interface ISubmissionSigner
{
    /// <summary>The signature scheme identifier stamped into the artifact (e.g. <c>"Ed25519"</c>).</summary>
    string SignatureAlgorithm { get; }

    /// <summary>An optional reference (key id / fingerprint) to the verifying public key.</summary>
    string? PublicKeyRef { get; }

    /// <summary>Signs the supplied DTBS hash, returning the signature bytes.</summary>
    /// <param name="dtbsHash">The digest of the canonical data-to-be-signed.</param>
    /// <param name="ct">Cancellation.</param>
    ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> dtbsHash, CancellationToken ct = default);
}
