using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Serialization;

/// <summary>
/// Canonicalizes a <see cref="PackContentSource"/> into a content-addressed
/// <see cref="PackContentItem"/>. This is the ONLY place pack content is canonicalized, and it
/// delegates to <see cref="CanonicalJson"/> — the SINGLE fleet canonicalizer already used by
/// ADR 0007-A1.2 and the roster/audit signing path (design invariant S-14: never a second
/// canonicalizer). The content address is a <see cref="Cid"/> (SHA-256), reusing the platform's
/// content-addressing primitive (architect fold A3).
/// </summary>
public sealed class PackContentCanonicalizer
{
    /// <summary>
    /// Canonicalizes <paramref name="source"/> to its byte-stable canonical form and computes the
    /// content address. Fails closed on ill-formed UTF-16 in the content (CanonicalJson rejects an
    /// unpaired surrogate rather than signing mangled bytes).
    /// </summary>
    public PackContentItem Canonicalize(PackContentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // CanonicalJson.Serialize sorts object keys, preserves array order, emits JCS-minimal
        // escaping — byte-identical to JS JSON.stringify — and fails closed on ill-formed UTF-16.
        // Serializing a JsonNode round-trips it through the same deterministic pipeline.
        var canonicalBytes = CanonicalJson.Serialize(source.Content);
        var address = Cid.FromBytes(canonicalBytes);

        return new PackContentItem(
            Key: source.Key,
            Kind: source.Kind,
            Version: source.Version,
            CanonicalBytes: canonicalBytes,
            ContentAddress: address);
    }
}
