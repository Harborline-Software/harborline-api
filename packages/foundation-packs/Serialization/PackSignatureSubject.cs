using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Serialization;

/// <summary>
/// The exact subject a pack signature covers: the <see cref="PackManifest"/> plus the signing
/// <see cref="Epoch"/>. This is the <c>payload</c> of the <c>SignedOperation&lt;PackSignatureSubject&gt;</c>
/// envelope, so the Ed25519 signature binds BOTH the manifest (and therefore every inner
/// content-address it lists — the merkle binding, S-14) AND the epoch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why epoch is bound in the SIGNED subject (not just an unsigned envelope field):</b> the
/// signature envelope carries <c>{key-id, epoch}</c> per ADR 0126 D4 sealed-epoch verification +
/// the MD-2 home-epoch fence (security fold S-11). <c>key-id</c> is the envelope's <c>IssuerId</c>
/// (the signing public key). <c>epoch</c> lives HERE, inside the signed payload, so tampering with
/// the claimed epoch invalidates the signature — an attacker cannot re-label a retired-epoch pack as
/// a current-epoch one.
/// </para>
/// <para>
/// The content-item BYTES are not part of this subject — only their ADDRESSES are (inside
/// <see cref="PackManifest.Contents"/>). Content integrity is therefore enforced by recomputing each
/// item's address at verify and comparing to this signed manifest (the merkle catch), not by signing
/// the bytes twice.
/// </para>
/// </remarks>
/// <param name="Manifest">The pack manifest (its content-addresses are transitively signed).</param>
/// <param name="Epoch">The signing epoch (ADR 0126 D4). Bound into the signature.</param>
public sealed record PackSignatureSubject(
    PackManifest Manifest,
    long Epoch);
