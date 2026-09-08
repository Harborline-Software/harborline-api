using System;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// The signed REVOCATION record that drops a member from a team's live trust roster — the syncable counterpart
/// of <see cref="AdmissionSignature"/> (roster-sync doctype; the foundational production-wiring gap #1). A
/// revocation is signed by an in-roster admin who holds <c>members:revoke</c>; it propagates across nodes as a
/// roster-sync record so a converged peer drops the revoked member from its live trusted set + attribution
/// binding (eventual-convergence — the offline window, <c>RevocationLivePathTests</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a SIGNED revocation (the trust anchor).</b> Without a signature a peer could not tell a real
/// revocation from a forged one — a bogus revocation delta would let any node drop any member (a
/// denial-of-trust injection). The signature binds the revocation to a revoker key, which the receiving node
/// validates is an in-roster admin holding <c>members:revoke</c> who chains to genesis — so a forged/unsigned
/// revocation is REJECTED fail-closed (same discipline as admission). <see cref="MemberRoster.Revoke"/> (the
/// LIVE local op from Phase B) drops a member without a signature because the caller is already the trusted
/// local admin; SYNC needs the signature because the receiver does not trust the sender's say-so.
/// </para>
/// <para>
/// <b>Genesis-immutable.</b> A revocation drops the target from LIVE state (the trusted/attribution set) but
/// NEVER touches the immutable admission chain — the target's admission stays logged so the chain still
/// validates through them (the canonical revoke-the-genesis-support-after-handoff case). Revoking the genesis
/// member from live state is allowed (subject to the no-bricking floor); un-genesising them is not possible.
/// </para>
/// </remarks>
/// <param name="RevokedByPublicKey">base64url of the revoking admin's Ed25519 public key (the signer).</param>
/// <param name="RevokedByPartyId">The revoking in-roster admin's party id (must hold members:revoke).</param>
/// <param name="IssuedAt">Wall-clock instant the revocation was issued (UTC).</param>
/// <param name="Nonce">Per-issuance nonce.</param>
/// <param name="Signature">base64url Ed25519 signature by the revoker over the canonical
/// <see cref="RevocationRecord"/> form.</param>
public sealed record RevocationSignature(
    string RevokedByPublicKey,
    string RevokedByPartyId,
    DateTimeOffset IssuedAt,
    Guid Nonce,
    string Signature);

/// <summary>
/// The canonical signable payload a <see cref="RevocationSignature"/> attests — the (team, revoked party)
/// tuple plus the revoker's identity. Pinning the property names keeps the canonical signed bytes stable
/// across signer + verifier (the same discipline <see cref="AdmissionRecord"/> uses). The revoked member's
/// (party, key) need not be re-stated beyond the party id: the receiving node looks the party up in its own
/// validated roster, so a revocation cannot be re-targeted by altering an unsigned key field.
/// </summary>
/// <param name="TeamId">The org/team this revocation is within (string form of the Guid).</param>
/// <param name="RevokedPartyId">The party being revoked from live state.</param>
/// <param name="RevokedByPartyId">The revoking in-roster admin's party id.</param>
/// <param name="RevokedByPublicKey">base64url of the revoking admin's public key.</param>
public sealed record RevocationRecord(
    string TeamId,
    string RevokedPartyId,
    string RevokedByPartyId,
    string RevokedByPublicKey);
