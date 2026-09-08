using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.UpdateFeed;

/// <summary>
/// The human-legible channel-root key-id form carried in the SIGNED feed payloads
/// (<c>channelRootKeyId</c>, design note §2.1/§2.2 — e.g. <c>ed25519:9f3c…</c>). It is a
/// <b>self-declaration for legibility, NOT the trust decision</b>: trust exists only if the envelope's
/// actual <see cref="SignedOperation{T}.IssuerId"/> matches a LOCALLY-pinned trust-store root (§3.1
/// F6). The verifier cross-checks that this declared id is consistent with the envelope's real signing
/// key, so a payload cannot claim one root while being signed by another.
/// </summary>
public static class FeedKeyId
{
    /// <summary>The algorithm prefix. v1 is Ed25519 only (the platform's single signature algorithm).</summary>
    public const string Ed25519Prefix = "ed25519:";

    /// <summary>Formats a signing key as its canonical feed key-id: <c>ed25519:&lt;base64url(pubkey)&gt;</c>.
    /// This is the FULL key (not a truncated fingerprint) so the id is unambiguous and self-consistent
    /// with the envelope's <see cref="PrincipalId"/>.</summary>
    public static string Format(PrincipalId keyId) => Ed25519Prefix + keyId.ToBase64Url();

    /// <summary>True iff <paramref name="declared"/> is exactly the key-id for <paramref name="actual"/> —
    /// the payload's self-declared root matches the envelope's real signer.</summary>
    public static bool Matches(string? declared, PrincipalId actual)
        => string.Equals(declared, Format(actual), StringComparison.Ordinal);
}
