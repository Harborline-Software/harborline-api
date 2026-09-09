using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// A single verified entry in a team's trust roster — one member's party id, signing public key,
/// and the admission record that signed them into the genesis-rooted log. The unit
/// <see cref="MemberRoster"/> validates to genesis and exposes for the trust gate (verified pubkey set) and
/// forge-proof attribution (the party→pubkey binding).
/// </summary>
/// <param name="PartyId">
/// The member's party id (the ADR 0032 ActorId/PartyId). <b>Ticket 294 slice 2a — the canonical party
/// is the canonical tenant PRINCIPAL id.</b> From this slice on this field carries
/// <c>CanonicalPartyBinding.PrincipalUserId.Value</c>: the same string the grant plane keys
/// <c>AccessGrant.Subject</c>, <c>GrantAuthorizationEpoch.PrincipalId</c> and every closure read by, and
/// the same string the wire carries as <c>JoiningPartyId</c>. One key, so the roster edge and the grant
/// store never have to be reconciled. The People <c>CanonicalPartyReference</c> does not disappear — it
/// keeps its job as the attribution stamped on what an act writes
/// (<c>SelectedSessionRequestPrincipal.CanonicalParty</c>) — it simply stops being an authorization key.
/// </param>
/// <param name="PublicKey">The member's Ed25519 public key — the party→pubkey binding.</param>
/// <param name="Admission">The signed admission that roots this member in the trust log.</param>
public sealed record RosterMember(
    string PartyId,
    PrincipalId PublicKey,
    AdmissionSignature Admission);
