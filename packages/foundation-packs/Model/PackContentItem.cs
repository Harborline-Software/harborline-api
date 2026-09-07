using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// A canonicalized, content-addressed pack content item — the output of running a
/// <see cref="PackContentSource"/> through the canonicalizer. Carries the exact canonical bytes that
/// were hashed (<see cref="CanonicalBytes"/>) and the resulting <see cref="ContentAddress"/>.
/// </summary>
/// <remarks>
/// The <see cref="ContentAddress"/> is <c>Cid.FromBytes(CanonicalBytes)</c> — the SHA-256 content
/// hash. Verification recomputes the CID over the bytes carried in the pack file and compares it to
/// the signed manifest reference; a mismatch is the merkle catch (S-14). The canonical bytes are the
/// SINGLE authority for the address — you cannot fake an address for different bytes.
/// </remarks>
/// <param name="Key">The stable content key within the pack.</param>
/// <param name="Kind">The declarative kind.</param>
/// <param name="Version">The pinned version.</param>
/// <param name="CanonicalBytes">The exact canonical UTF-8 bytes that were hashed and signed-over
/// (via the manifest address).</param>
/// <param name="ContentAddress">The content-address (SHA-256 CID) of <see cref="CanonicalBytes"/>.</param>
public sealed record PackContentItem(
    string Key,
    PackContentKind Kind,
    string Version,
    ReadOnlyMemory<byte> CanonicalBytes,
    Cid ContentAddress);
