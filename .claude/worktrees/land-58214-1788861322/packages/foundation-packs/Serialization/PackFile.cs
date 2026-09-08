using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Serialization;

/// <summary>
/// The on-disk / on-the-wire pack file — a single, SNEAKERNET-able artifact (carry it on a USB
/// stick, verify it offline at install; design §2.4 / S-5). It carries the signed
/// <see cref="Envelope"/> (manifest + epoch + signature + key-id) and the raw content-item
/// <see cref="Contents"/> whose addresses the manifest binds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nullable envelope = "not signed".</b> A file whose <see cref="Envelope"/> is <c>null</c> is an
/// UNSIGNED pack; verification returns <c>NotSigned</c> (v1 install is fail-closed — an unsigned pack
/// is never trusted, but it is a distinct, honest verdict from a tamper). A present envelope with a
/// bad signature is <c>VerificationFailed</c>.
/// </para>
/// <para>
/// The content payloads live OUTSIDE the signed envelope because they are addressed, not signed — see
/// <see cref="PackSignatureSubject"/>. Verification recomputes each payload's address and matches it
/// against the signed manifest; the raw bytes never need to be trusted on-wire (S-14: re-canonicalize
/// / re-hash, never trust the file's byte layout).
/// </para>
/// </remarks>
/// <param name="Envelope">The signed envelope (manifest + epoch), or <c>null</c> for an unsigned
/// pack.</param>
/// <param name="Contents">The raw content-item payloads (base64 canonical bytes).</param>
/// <param name="Dcp">The raw Domain Compliance Profile payload (base64 canonical bytes), or <c>null</c>
/// for a legacy/pre-DCP pack. Like the content payloads it lives OUTSIDE the signed envelope because it is
/// ADDRESSED, not signed — verification re-hashes these bytes and matches the signed
/// <c>PackManifest.Dcp</c> content-address (the DCP merkle catch; ADR 0145 D3.4).</param>
public sealed record PackFile(
    SignedOperation<PackSignatureSubject>? Envelope,
    IReadOnlyList<PackContentPayload> Contents,
    PackDcpPayload? Dcp = null);

/// <summary>
/// One content item as carried in a <see cref="PackFile"/> — the item's canonical bytes, base64
/// encoded. The <see cref="ContentBase64"/> bytes are re-hashed at verify to reproduce the item's
/// content-address (the merkle leaf); no address is trusted from the file.
/// </summary>
/// <param name="Key">The stable content key within the pack.</param>
/// <param name="Kind">The declarative kind.</param>
/// <param name="Version">The pinned version.</param>
/// <param name="ContentBase64">Base64 of the item's canonical UTF-8 bytes.</param>
public sealed record PackContentPayload(
    string Key,
    PackContentKind Kind,
    string Version,
    string ContentBase64);

/// <summary>
/// The pack's Domain Compliance Profile as carried in a <see cref="PackFile"/> — the DCP's canonical
/// bytes, base64 encoded (ADR 0145 D3.4). The <see cref="ContentBase64"/> bytes are re-hashed at verify to
/// reproduce the DCP's content-address (its merkle leaf) and matched against the signed
/// <c>PackManifest.Dcp</c>; no address is trusted from the file.
/// </summary>
/// <param name="ContentBase64">Base64 of the DCP's canonical UTF-8 bytes.</param>
public sealed record PackDcpPayload(string ContentBase64);
