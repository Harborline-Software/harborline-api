using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// A manifest reference to one content item — a MERKLE LEAF. The reference carries the item's
/// stable content <see cref="Key"/> (within the pack), its <see cref="Kind"/>, a PINNED
/// <see cref="Version"/> (never a mutable id), and the <see cref="ContentAddress"/> — the
/// <see cref="Cid"/> (SHA-256 content hash) of the item's canonical bytes.
/// </summary>
/// <remarks>
/// <para>
/// The manifest's list of these refs is what the signature covers (the ref lives inside the signed
/// <c>PackSignatureSubject.Manifest</c>). Because the reference carries the CONTENT ADDRESS and the
/// signature covers the manifest, signing the manifest binds every inner content address — so
/// substituting an inner item changes its recomputed address, which no longer matches the signed
/// reference, and verification catches it (design invariant S-14: "the pack envelope merkle-binds
/// the inner content addresses, so item substitution is structurally impossible").
/// </para>
/// <para>
/// The stable <see cref="Key"/> is what a later upgrade re-attaches tenant overrides against
/// (ADR 0011 three-way merge; S-10 total re-attach) — a B-1b concern; B-1a only pins it.
/// </para>
/// </remarks>
/// <param name="Key">The stable content key of the item within the pack (upgrade-stable; S-10).</param>
/// <param name="Kind">The declarative kind of the item.</param>
/// <param name="Version">The PINNED version of the item (never a mutable id; ADR 0129 D1).</param>
/// <param name="ContentAddress">The content-address (SHA-256 CID) of the item's canonical bytes.</param>
public sealed record PackContentRef(
    string Key,
    PackContentKind Kind,
    string Version,
    Cid ContentAddress);
