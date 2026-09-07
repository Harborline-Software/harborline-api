using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// A single verified entry in a team's trust roster — one member's party id, signing public key, held
/// permission set, and the admission record that signed them into the genesis-rooted log. The unit
/// <see cref="MemberRoster"/> validates to genesis and exposes for the trust gate (verified pubkey set) and
/// forge-proof attribution (the party→pubkey binding).
/// </summary>
/// <param name="PartyId">The member's party id (the ADR 0032 ActorId/PartyId).</param>
/// <param name="PublicKey">The member's Ed25519 public key — the party→pubkey binding.</param>
/// <param name="Permissions">The member's MUTABLE held permission set (the PBAC truth on the edge).</param>
/// <param name="Admission">The signed admission that roots this member in the trust log.</param>
public sealed record RosterMember(
    string PartyId,
    PrincipalId PublicKey,
    PermissionSet Permissions,
    AdmissionSignature Admission);
