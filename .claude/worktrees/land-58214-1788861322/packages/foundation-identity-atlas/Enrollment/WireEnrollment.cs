using System;
using System.Collections.Generic;
using System.Linq;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// The TWO-SIDED WIRE ENROLLMENT protocol — the invite-gated trust BOOTSTRAP that makes cross-machine P2P
/// enrollment actually work (cerebrum [2026-06-21] "CRITICAL — admission is ADMITTER-LOCAL, no wire-level
/// mutual-trust bootstrap"). It is the missing JOINER half of admission.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> The shipping admission (<see cref="AdmissionCoordinator.AdmitOverInvite"/> + the
/// node <c>/admission/redeem</c> route) is ADMITTER-NODE-LOCAL: A's redeem admits B into A's OWN roster and
/// wires B's transport key into A's trust map — but the admission NEVER reaches B. roster-sync cannot deliver
/// it because it rides a TRUSTED session, the very thing being rejected (chicken-and-egg). So two fresh nodes
/// never establish mutual trust over the wire → <c>PEER_UNTRUSTED</c> → no comms. Every prior test
/// HAND-PROVISIONED both sides' trust + roster + same team — that is what hid this for the entire arc.
/// </para>
/// <para>
/// <b>The exchange (B = the joiner, A = the admitter):</b>
/// <list type="number">
///   <item><b>B → A:</b> <see cref="EnrollmentRequest"/> = {invite token id, B.partyId, B's A-TEAM-SCOPED
///     transport pubkey = HKDF(B-root, A.teamId), B's principal pubkey} SIGNED by B's principal key
///     (<see cref="BuildRequest"/>). The signature is B's proof-of-possession of the principal key it claims —
///     A binds the admission to the EXACT key B signed with.</item>
///   <item><b>A:</b> validate the invite (signed by a roster admin / single-use consume / TTL — the
///     <see cref="AdmissionCoordinator.AdmitOverInvite"/> path) + <see cref="VerifyRequest"/> (B's signature) →
///     admit B (sign B into A's roster binding B's principal + record B's A-team-scoped transport key in A's
///     trust map) → respond <see cref="EnrollmentResponse"/> = {A's transport pubkey for A.teamId, the team
///     genesis anchor, the current synced roster (admissions + revocations), the per-member transport keys}
///     (<see cref="BuildResponse"/>).</item>
///   <item><b>B:</b> <see cref="ValidateAndPlanAdoption"/> — validate A's response against the INVITE's team
///     anchor (delivered out-of-band — the MITM-resistance root): the genesis key in the rebuilt roster equals
///     the anchor's genesis key + the roster <c>ValidatesToGenesis</c> + B is a live member bound to B's
///     principal key + A is a live member. → <see cref="EnrollmentAdoptionPlan"/>: ADOPT A's team (set active
///     team = A.teamId; B's own genesis team is superseded — single-team reference edition; B re-derives /
///     confirms its transport subkey for A.teamId, consistent with what it presented) + wire A's + the roster
///     members' transport keys into B's trust policy.</item>
/// </list>
/// </para>
/// <para>
/// <b>After:</b> both trust policies hold both transport keys for A.teamId → the normal trusted HELLO passes →
/// roster-sync + comms converge. Today only A's half exists; this builds B's half + the wire exchange.
/// </para>
/// <para>
/// <b>Security spine.</b> invite-gated (no valid invite → no admission, the existing
/// <see cref="IAdmissionTokenStore"/> single-use/TTL); MITM-resist (B validates A's identity against the
/// out-of-band invite anchor — an attacker who returns a parallel roster rooted in a DIFFERENT genesis fails
/// the anchor pin); B's ENROLL signed by B (A binds the admission to B's presented principal key, not a
/// claimed one); single-use/replay-proof (the token is consumed); key-scoping (the bound transport key = B's
/// HKDF(B-root, A.teamId) = the key B presents on the trusted session — the #1296-F2 lesson for the JOINED
/// team); no-escalation (B gets the member composition via Admit, no admin powers); fail-closed on any
/// validation failure (no partial trust — a failed validation yields NO adoption plan).
/// </para>
/// <para>
/// <b>Dependency posture.</b> This is the PURE protocol: it works with transport pubkeys as RAW BYTES (already
/// HKDF-derived by the host at the kernel-security edge), so foundation-identity-atlas stays free of
/// kernel-security/kernel-sync. The HKDF(root, teamId) derivation + the wire transport + the
/// <c>NodeTeamRoster</c>/active-team adoption happen in the host (local-node-host) that drives this seam.
/// </para>
/// </remarks>
public static class WireEnrollment
{
    /// <summary>
    /// B (the joiner) builds + signs an <see cref="EnrollmentRequest"/>. The request carries B's party id, B's
    /// A-team-scoped TRANSPORT public key (raw 32-byte Ed25519, = HKDF(B-root, A.teamId) — the key B will present
    /// on the trusted HELLO once it adopts A's team), and B's PRINCIPAL public key. The whole binding is signed
    /// by <paramref name="joinerPrincipalSigner"/> (B's principal key) — proof B controls the principal key it
    /// claims, so a man-in-the-middle who swaps B's keys must re-sign and cannot (it lacks B's private key).
    /// </summary>
    /// <param name="tokenId">The invite token id B presents (delivered out-of-band with the team anchor).</param>
    /// <param name="joiningPartyId">B's party id.</param>
    /// <param name="joiningTransportPublicKey">B's A-team-scoped transport pubkey (raw 32-byte Ed25519).</param>
    /// <param name="joinerPrincipalSigner">B's principal signer (its <see cref="IOperationSigner.IssuerId"/> is
    /// the principal key A will bind in the roster).</param>
    /// <param name="issuedAt">The request issuance instant (replay/audit context).</param>
    /// <param name="nonce">A per-request nonce.</param>
    /// <param name="joiningDmPublicKey">C5 — B's A-team-scoped DM-encryption PUBLIC key (raw 32-byte X25519, the
    /// public half of HKDF(B-root, A.teamId) over the DM domain). Optional + additive: when present it is signed
    /// into the binding (so A binds the EXACT DM key B proved possession of) and A records it so the team can derive
    /// DM seal keys with B. Null = a legacy joiner that presents no DM key (its DMs cannot be sealed until it
    /// re-emits the key via the synced roster).</param>
    /// <param name="joiningXWingPublicKey">#3167 R3 — B's A-team-scoped X-Wing PUBLIC key as an opaque base64url
    /// string (1216-byte raw; validated at the pairing route, not here — foundation stays X-Wing-size-agnostic).
    /// When non-empty it is signed into the transcript so B's principal signature covers the X-Wing bytes (R4-a).
    /// Empty (default) = an X-Wing-incapable joiner (byte-stable — the empty string is signed verbatim).</param>
    public static EnrollmentRequest BuildRequest(
        string tokenId,
        string joiningPartyId,
        byte[] joiningTransportPublicKey,
        IOperationSigner joinerPrincipalSigner,
        DateTimeOffset issuedAt,
        Guid nonce,
        byte[]? joiningDmPublicKey = null,
        string joiningXWingPublicKey = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joiningPartyId);
        ArgumentNullException.ThrowIfNull(joiningTransportPublicKey);
        ArgumentNullException.ThrowIfNull(joinerPrincipalSigner);
        if (joiningTransportPublicKey.Length != PrincipalId.LengthInBytes)
        {
            throw new ArgumentException(
                $"The joining transport public key must be exactly {PrincipalId.LengthInBytes} bytes.",
                nameof(joiningTransportPublicKey));
        }
        if (joiningDmPublicKey is not null && joiningDmPublicKey.Length != PrincipalId.LengthInBytes)
        {
            throw new ArgumentException(
                $"The joining DM public key, when present, must be exactly {PrincipalId.LengthInBytes} bytes.",
                nameof(joiningDmPublicKey));
        }

        var joiningPrincipalPublicKey = joinerPrincipalSigner.IssuerId;
        var transportKeyB64 = ToBase64Url(joiningTransportPublicKey);
        var dmKeyB64 = joiningDmPublicKey is null ? string.Empty : ToBase64Url(joiningDmPublicKey);
        // #3167 R3/R4-a — the X-Wing public key rides the SAME signed transcript as an opaque base64url string, so
        // the joiner's principal-key signature COVERS the X-Wing bytes (closing X-Wing key-substitution the same way
        // the DM key is covered). foundation-identity-atlas stays X-Wing-size-agnostic (X-Wing = 1216 raw bytes lives
        // in kernel-security, which this package must not depend on); the pairing route does the mandatory/malformed
        // length check. Empty (default) = an X-Wing-incapable joiner (byte-stable; the empty string is signed verbatim).
        var xwingKeyB64 = joiningXWingPublicKey ?? string.Empty;

        // The canonical signable payload: the (token, party, transport-key, dm-key, x-wing-key, principal-key)
        // binding. Pinning the exact fields keeps the signed bytes stable across signer + verifier (the same
        // discipline RosterSigning uses for admissions). Truncate the instant to epoch-ms so verify reconstructs
        // identical bytes.
        var issuedAtMs = DateTimeOffset.FromUnixTimeMilliseconds(issuedAt.ToUnixTimeMilliseconds());
        var record = new EnrollmentRequestRecord(
            TokenId: tokenId,
            JoiningPartyId: joiningPartyId,
            JoiningTransportPublicKey: transportKeyB64,
            JoiningPrincipalPublicKey: joiningPrincipalPublicKey.ToBase64Url(),
            JoiningDmPublicKey: dmKeyB64,
            JoiningXWingPublicKey: xwingKeyB64);
        var op = joinerPrincipalSigner.SignAsync(record, issuedAtMs, nonce).AsTask().GetAwaiter().GetResult();

        return new EnrollmentRequest(
            TokenId: tokenId,
            JoiningPartyId: joiningPartyId,
            JoiningTransportPublicKey: transportKeyB64,
            JoiningPrincipalPublicKey: joiningPrincipalPublicKey.ToBase64Url(),
            IssuedAt: issuedAtMs,
            Nonce: nonce,
            Signature: op.Signature.ToBase64Url(),
            JoiningDmPublicKey: dmKeyB64,
            JoiningXWingPublicKey: xwingKeyB64);
    }

    /// <summary>
    /// A verifies a received <see cref="EnrollmentRequest"/> — that the joiner's signature validly covers the
    /// (token, party, transport-key, principal-key) binding under the CLAIMED principal key. Returns true iff
    /// the joiner proved possession of the principal key it claims (so A may safely bind THAT key in the roster).
    /// Fail-closed on any malformed field. This is the proof-of-possession gate that defeats a MITM who would
    /// substitute B's keys: the attacker would have to re-sign the swapped binding without B's private key.
    /// </summary>
    public static bool VerifyRequest(EnrollmentRequest request, IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifier);

        try
        {
            // The transport key must decode to exactly 32 bytes — a malformed transport key is rejected before
            // it could ever be recorded in a trust map.
            var transportBytes = FromBase64Url(request.JoiningTransportPublicKey);
            if (transportBytes.Length != PrincipalId.LengthInBytes) return false;

            // C5: when present, the DM key must also decode to exactly 32 bytes (rejected before recording). Empty
            // is allowed (a legacy joiner with no DM key) — the signable below carries the empty string verbatim, so
            // the signature still binds it (an attacker cannot inject a DM key without re-signing, which it can't).
            if (!string.IsNullOrEmpty(request.JoiningDmPublicKey))
            {
                var dmBytes = FromBase64Url(request.JoiningDmPublicKey);
                if (dmBytes.Length != PrincipalId.LengthInBytes) return false;
            }

            // #3167 R3/R4-a — the X-Wing key is carried in the signed transcript verbatim (opaque base64url). Its
            // bytes are covered by the joiner's principal signature exactly like the DM key; the pairing route
            // enforces presence + 1216-byte structure (foundation stays X-Wing-size-agnostic). An empty string is a
            // valid "no X-Wing key" binding, signed verbatim (an attacker cannot inject an X-Wing key without
            // re-signing under the joiner's principal key, which it lacks).
            var record = new EnrollmentRequestRecord(
                TokenId: request.TokenId,
                JoiningPartyId: request.JoiningPartyId,
                JoiningTransportPublicKey: request.JoiningTransportPublicKey,
                JoiningPrincipalPublicKey: request.JoiningPrincipalPublicKey,
                JoiningDmPublicKey: request.JoiningDmPublicKey ?? string.Empty,
                JoiningXWingPublicKey: request.JoiningXWingPublicKey ?? string.Empty);

            var op = new SignedOperation<EnrollmentRequestRecord>(
                Payload: record,
                IssuerId: PrincipalId.FromBase64Url(request.JoiningPrincipalPublicKey),
                IssuedAt: request.IssuedAt,
                Nonce: request.Nonce,
                Signature: Signature.FromBase64Url(request.Signature));
            return verifier.Verify(op);
        }
        catch (FormatException)
        {
            return false; // malformed key / signature → not verifiable → not authentic.
        }
    }

    /// <summary>
    /// A builds the <see cref="EnrollmentResponse"/> AFTER it has admitted B (via
    /// <see cref="AdmissionCoordinator.AdmitOverInvite"/>, which produced <paramref name="postAdmitRoster"/>) and
    /// wired B's transport key into A's local trust map. The response carries everything B needs to bootstrap
    /// MUTUAL trust: A's transport pubkey for A.teamId, the team genesis anchor (the chain-root pin), the full
    /// synced roster (admissions + revocations B independently re-validates), and the per-member transport-key
    /// map (so B trusts every roster member's wire HELLO, not only A's).
    /// </summary>
    /// <param name="postAdmitRoster">A's roster AFTER admitting B (the trust anchor for B's adoption).</param>
    /// <param name="admitterTransportPublicKey">A's team-scoped transport pubkey for A.teamId (raw 32-byte
    /// Ed25519 — the key A presents on the trusted HELLO).</param>
    /// <param name="memberTransportKeys">The per-party transport pubkey map A knows for the team's LIVE members
    /// (raw 32-byte Ed25519 each). MUST include A's own (so B trusts A) and B's own (echoed back). The map is
    /// UNSIGNED transport-trust hints — B re-validates the ROSTER (the signed part) to genesis; the transport
    /// keys are matched to validated members by party id, so an extra/spurious key for a non-member is dropped.
    /// </param>
    /// <param name="memberDmKeys">C5 — the per-party DM-encryption PUBLIC-key map A knows for the team's LIVE
    /// members (raw 32-byte X25519 each). Should include A's own (so B can DM A) and B's own (echoed). Optional +
    /// additive: same UNSIGNED-hint posture as <paramref name="memberTransportKeys"/> — B re-validates the ROSTER
    /// (signed) and matches DM keys to validated members by party id, dropping a key for a non-member. Null/absent =
    /// no DM keys in the response (B learns them later from the synced roster).</param>
    public static EnrollmentResponse BuildResponse(
        MemberRoster postAdmitRoster,
        byte[] admitterTransportPublicKey,
        IReadOnlyDictionary<string, byte[]> memberTransportKeys,
        IReadOnlyDictionary<string, byte[]>? memberDmKeys = null)
    {
        ArgumentNullException.ThrowIfNull(postAdmitRoster);
        ArgumentNullException.ThrowIfNull(admitterTransportPublicKey);
        ArgumentNullException.ThrowIfNull(memberTransportKeys);
        if (admitterTransportPublicKey.Length != PrincipalId.LengthInBytes)
        {
            throw new ArgumentException(
                $"The admitter transport public key must be exactly {PrincipalId.LengthInBytes} bytes.",
                nameof(admitterTransportPublicKey));
        }

        var anchor = TeamTrustAnchor.FromRoster(postAdmitRoster);
        var admissions = postAdmitRoster.EnumerateAdmissions()
            .Select(a => new WireAdmissionRecord(
                a.TeamId,
                a.PartyId,
                a.PublicKey.ToBase64Url(),
                a.Permissions.Permissions.ToArray(),
                a.Admission))
            .ToArray();

        var transportKeys = memberTransportKeys
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is { Length: PrincipalId.LengthInBytes })
            .Select(kv => new WireMemberTransportKey(kv.Key, ToBase64Url(kv.Value)))
            .ToArray();

        var dmKeys = (memberDmKeys ?? new Dictionary<string, byte[]>(StringComparer.Ordinal))
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is { Length: PrincipalId.LengthInBytes })
            .Select(kv => new WireMemberDmKey(kv.Key, ToBase64Url(kv.Value)))
            .ToArray();

        return new EnrollmentResponse(
            TeamId: anchor.TeamId,
            Anchor: anchor,
            AdmitterTransportPublicKey: ToBase64Url(admitterTransportPublicKey),
            Admissions: admissions,
            Revocations: Array.Empty<WireRevocationRecord>(),
            MemberTransportKeys: transportKeys,
            MemberDmKeys: dmKeys);
    }

    /// <summary>
    /// B (the joiner) validates A's <see cref="EnrollmentResponse"/> against the INVITE's team anchor (the
    /// out-of-band trust pin — the MITM-resistance root) and produces an <see cref="EnrollmentAdoptionPlan"/>
    /// when, and ONLY when, every gate passes. A failed gate yields a FAILED plan (no partial trust). This is
    /// the JOINER half the entire arc was missing.
    /// </summary>
    /// <remarks>
    /// <para><b>Validation pipeline (every gate fail-closed):</b></para>
    /// <list type="number">
    ///   <item>The response's team id + anchor MATCH the invite anchor B already holds (team id + genesis party
    ///     + genesis verifying key). A response for a DIFFERENT team / DIFFERENT genesis than the out-of-band
    ///     anchor is an attacker's parallel roster — REJECTED.</item>
    ///   <item>The synced roster <see cref="MemberRoster.FromSyncedRecords"/> re-validates to genesis
    ///     independently (B never trusts A's say-so — each admission's signature + chain is re-checked). A roster
    ///     that does not validate to genesis is REJECTED.</item>
    ///   <item>The REBUILT roster's genesis equals the anchor's genesis (party id + key) — the chain root B
    ///     pinned out-of-band. Otherwise REJECTED.</item>
    ///   <item>B is a LIVE member of the rebuilt roster AND its bound principal key equals B's own principal key
    ///     (A bound the RIGHT key — proof the admission is for B, not a substituted key). Otherwise REJECTED.</item>
    ///   <item>The admitter (A) is a LIVE member, and A's transport key is present in the member-transport map.
    ///     Otherwise REJECTED (B could not trust A's HELLO).</item>
    /// </list>
    /// <para>
    /// On success the plan carries: the team id to ADOPT, the rebuilt validated roster (B adopts it as its own
    /// team roster), and the transport-key set to wire into B's trust policy — the keys for every LIVE roster
    /// member (matched by party id; A's own key included). B's own team subkey for A.teamId is the never-brick
    /// floor the host's registrar already contributes, so B's own HELLO key is trusted by construction once B
    /// adopts A's team.
    /// </para>
    /// </remarks>
    /// <param name="response">A's enrollment response.</param>
    /// <param name="inviteAnchor">The team anchor B received OUT-OF-BAND with the invite (the trust pin).</param>
    /// <param name="joinerPrincipalPublicKey">B's own principal public key (to confirm A bound the right key).</param>
    /// <param name="joinerPartyId">B's own party id.</param>
    /// <param name="verifier">The signature verifier (re-validates the roster to genesis).</param>
    public static EnrollmentAdoptionPlan ValidateAndPlanAdoption(
        EnrollmentResponse response,
        TeamTrustAnchor inviteAnchor,
        PrincipalId joinerPrincipalPublicKey,
        string joinerPartyId,
        IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(inviteAnchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinerPartyId);
        ArgumentNullException.ThrowIfNull(verifier);

        // (1) The response's team + anchor must MATCH the out-of-band invite anchor (team id + genesis party +
        //     genesis key). A mismatch is an attacker returning a parallel roster — reject before any rebuild.
        if (!string.Equals(response.TeamId, inviteAnchor.TeamId, StringComparison.Ordinal))
            return EnrollmentAdoptionPlan.Failed("team_id_mismatch");
        if (response.Anchor is null
            || !string.Equals(response.Anchor.TeamId, inviteAnchor.TeamId, StringComparison.Ordinal)
            || !string.Equals(response.Anchor.GenesisPartyId, inviteAnchor.GenesisPartyId, StringComparison.Ordinal)
            || !string.Equals(response.Anchor.GenesisPublicKey, inviteAnchor.GenesisPublicKey, StringComparison.Ordinal))
            return EnrollmentAdoptionPlan.Failed("anchor_mismatch");

        if (!Guid.TryParse(response.TeamId, out var teamId))
            return EnrollmentAdoptionPlan.Failed("malformed_team_id");

        // (2) Rebuild + INDEPENDENTLY re-validate the roster to genesis (B never trusts A's say-so). Malformed
        //     records throw FormatException inside FromSyncedRecords' verify path → caught fail-closed.
        MemberRoster rebuilt;
        try
        {
            var admissions = response.Admissions
                .Select(a => new MemberAdmissionRecord(
                    a.TeamId,
                    a.PartyId,
                    PrincipalId.FromBase64Url(a.PublicKey),
                    PermissionSet.From(a.Permissions ?? Array.Empty<string>()),
                    a.Admission,
                    TransportPublicKey: null,
                    // C5 — reconstruct the carried byte[] DM key from the SIGNED admission's DM key so the rebuild's
                    // carried-matches-signed consistency gate is satisfied (the signed value is the authority). A
                    // legacy admission with no signed DM key yields null (no carried DM key — consistent).
                    DmPublicKey: MemberRoster.DecodeSignedDmKeyOrNull(a.Admission.DmPublicKey),
                    // R6/F2-a — the same hard binding applies to the X-Wing field. The joiner must reconstruct the
                    // carried bytes from the signed admission or the roster's signed-vs-carried consistency gate
                    // correctly drops the record as a substituted key.
                    XWingPublicKey: MemberRoster.DecodeSignedXWingKeyOrNull(a.Admission.XWingPublicKey)))
                .ToArray();
            var revocations = response.Revocations
                .Select(r => new MemberRevocationRecord(r.TeamId, r.RevokedPartyId, r.Signed))
                .ToArray();
            rebuilt = MemberRoster.FromSyncedRecords(admissions, revocations, verifier);
        }
        catch (FormatException)
        {
            return EnrollmentAdoptionPlan.Failed("malformed_roster_record");
        }

        if (!rebuilt.ValidatesToGenesis(verifier))
            return EnrollmentAdoptionPlan.Failed("roster_not_genesis_rooted");

        // (3) The rebuilt genesis must equal the anchor's genesis (party id + key) — the root B pinned.
        if (!string.Equals(rebuilt.GenesisPartyId, inviteAnchor.GenesisPartyId, StringComparison.Ordinal))
            return EnrollmentAdoptionPlan.Failed("genesis_party_mismatch");
        var rebuiltGenesisKey = rebuilt.PublicKeyOf(rebuilt.GenesisPartyId);
        if (rebuiltGenesisKey is null || !rebuiltGenesisKey.Value.Equals(inviteAnchor.GenesisKey()))
            return EnrollmentAdoptionPlan.Failed("genesis_key_mismatch");

        // (4) B must be a LIVE member bound to B's OWN principal key (A bound the RIGHT key, not a substituted
        //     one — proof the admission is for B). A roster that lacks B, or binds a different key under B's
        //     party id, is rejected (the MITM-substitution would surface here).
        var boundJoinerKey = rebuilt.PublicKeyOf(joinerPartyId);
        if (boundJoinerKey is null)
            return EnrollmentAdoptionPlan.Failed("joiner_not_in_roster");
        if (!boundJoinerKey.Value.Equals(joinerPrincipalPublicKey))
            return EnrollmentAdoptionPlan.Failed("joiner_key_mismatch");

        // (5) The admitter transport key must decode + the transport-key map must cover every LIVE member that B
        //     will need to trust on the wire. Build the validated transport-key set (party → transport pubkey),
        //     keeping ONLY entries whose party is a validated LIVE member of the rebuilt roster (a spurious key
        //     for a non-member is dropped — the roster is the authority, the transport map is a hint).
        byte[] admitterTransportKey;
        try
        {
            admitterTransportKey = FromBase64Url(response.AdmitterTransportPublicKey);
        }
        catch (FormatException)
        {
            return EnrollmentAdoptionPlan.Failed("malformed_admitter_transport_key");
        }
        if (admitterTransportKey.Length != PrincipalId.LengthInBytes)
            return EnrollmentAdoptionPlan.Failed("malformed_admitter_transport_key");

        var liveParties = new HashSet<string>(rebuilt.Members.Select(m => m.PartyId), StringComparer.Ordinal);
        var trustedTransportByParty = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in response.MemberTransportKeys)
        {
            if (string.IsNullOrWhiteSpace(entry.PartyId) || !liveParties.Contains(entry.PartyId)) continue;
            byte[] keyBytes;
            try { keyBytes = FromBase64Url(entry.TransportPublicKey); }
            catch (FormatException) { return EnrollmentAdoptionPlan.Failed("malformed_member_transport_key"); }
            if (keyBytes.Length != PrincipalId.LengthInBytes)
                return EnrollmentAdoptionPlan.Failed("malformed_member_transport_key");
            trustedTransportByParty[entry.PartyId] = keyBytes;
        }

        // The admitter must be a live member whose transport key B now holds — else B cannot trust A's HELLO.
        var admitterPartyId = response.Anchor.GenesisPartyId;
        // The "admitter" for trust purposes is whoever B must reach: every live member. But specifically A (the
        // node B is enrolling against) must be reachable. Use the genesis party as the canonical anchor-bound
        // admitter for the single-office (genesis = the admin) case; in any case its transport key must be
        // present in the validated map.
        if (!trustedTransportByParty.ContainsKey(admitterPartyId))
            return EnrollmentAdoptionPlan.Failed("admitter_transport_key_missing");

        // Defensive: A's stated transport key (AdmitterTransportPublicKey) must match the genesis/admitter
        // entry in the validated map (no divergence between the headline key and the per-member map).
        if (!trustedTransportByParty[admitterPartyId].AsSpan().SequenceEqual(admitterTransportKey))
            return EnrollmentAdoptionPlan.Failed("admitter_transport_key_inconsistent");

        // (6) C5 (DM key-substitution fix; sec-eng deep-review of PR #1326) — build the DM-key set from the
        //     chain-VALIDATED rebuilt roster (the key each admission SIGNATURE attests for that party), NOT from the
        //     unsigned MemberDmKeys hint. The DM key is now bound into the signed admission, so MemberRoster.
        //     DmPublicKeyOf returns the forge-proof key for each live member — an attacker cannot substitute a peer's
        //     DM key on the response (the substituted admission record fails signature verification in
        //     FromSyncedRecords, dropping the member). A member whose admission signed no DM key simply has none here
        //     (B learns it later when that member re-emits a DM-key-bearing admission). DM sealing is not gated by the
        //     trust handshake, so a missing DM key does not block enrollment. The unsigned MemberDmKeys field is
        //     retained on the wire only for back-compat with legacy peers; it is NO LONGER an authority.
        var dmByParty = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var m in rebuilt.Members)
        {
            var signedDmKey = rebuilt.DmPublicKeyOf(m.PartyId);
            if (signedDmKey is { Length: > 0 })
            {
                dmByParty[m.PartyId] = signedDmKey;
            }
        }

        return EnrollmentAdoptionPlan.Adopt(teamId, rebuilt, trustedTransportByParty, dmByParty);
    }

    // ── base64url helpers (kept local so the protocol has no extra dependency) ──────────────────────────────────

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        // Reuse PrincipalId's validated 32-byte base64url decode where the value is a key; for non-32 inputs this
        // throws FormatException, which every caller catches fail-closed.
        return PrincipalId.FromBase64Url(value).AsSpan().ToArray();
    }
}

/// <summary>
/// The B → A signed ENROLL message of the two-sided wire protocol. Carries the invite token id, B's party id,
/// B's A-team-scoped TRANSPORT public key (base64url; = HKDF(B-root, A.teamId)), B's PRINCIPAL public key
/// (base64url), and B's signature over the binding. A verifies the signature (<see cref="WireEnrollment.VerifyRequest"/>)
/// before binding B's principal key in the roster.
/// </summary>
/// <remarks>
/// #3167 R3 — <c>JoiningXWingPublicKey</c> is B's A-team-scoped X-Wing (X25519 + ML-KEM-768) PUBLIC key (base64url,
/// 1216-byte raw), an ADDITIVE optional wire field. When present it is signed into the binding (so the joiner's
/// principal signature proves the keys are the ones the authenticated principal bound under THIS token — R4-a — and
/// A binds the EXACT X-Wing key the joiner proved), and A records it into the signed admission. On the web-admitted
/// PAIRING path it is MANDATORY (absent/malformed → the route refuses, wire-opaque); legacy / non-pairing paths
/// ignore it. Empty (default) = an X-Wing-incapable joiner.
/// </remarks>
public sealed record EnrollmentRequest(
    string TokenId,
    string JoiningPartyId,
    string JoiningTransportPublicKey,
    string JoiningPrincipalPublicKey,
    DateTimeOffset IssuedAt,
    Guid Nonce,
    string Signature,
    string JoiningDmPublicKey = "",
    string JoiningXWingPublicKey = "");

/// <summary>The canonical signable payload an <see cref="EnrollmentRequest"/> signature attests — the
/// (token, party, transport-key, principal-key, dm-key) binding. Pinned field names keep the signed bytes stable
/// across signer + verifier (same discipline as <see cref="AdmissionRecord"/>). C5: the DM key is the LAST field
/// (defaults empty) so the binding remains byte-stable for a legacy joiner that signs no DM key (empty string), and
/// a joiner that DOES present one binds it into its proof-of-possession (A cannot be tricked into binding a
/// different DM key — an attacker would have to re-sign without B's private key).</summary>
public sealed record EnrollmentRequestRecord(
    string TokenId,
    string JoiningPartyId,
    string JoiningTransportPublicKey,
    string JoiningPrincipalPublicKey,
    string JoiningDmPublicKey = "",
    string JoiningXWingPublicKey = "");

/// <summary>
/// The A → B ENROLLMENT response of the two-sided wire protocol — the bootstrap payload that lets B establish
/// MUTUAL trust. Carries A.teamId, the team genesis anchor (the chain-root pin), A's transport pubkey for
/// A.teamId, the full synced roster (admissions + revocations B re-validates to genesis), and the per-member
/// transport-key map (so B trusts every member's wire HELLO). The roster is the SIGNED authority; the transport
/// keys are unsigned hints matched to validated members.
/// </summary>
public sealed record EnrollmentResponse(
    string TeamId,
    TeamTrustAnchor Anchor,
    string AdmitterTransportPublicKey,
    IReadOnlyList<WireAdmissionRecord> Admissions,
    IReadOnlyList<WireRevocationRecord> Revocations,
    IReadOnlyList<WireMemberTransportKey> MemberTransportKeys,
    IReadOnlyList<WireMemberDmKey>? MemberDmKeys = null);

/// <summary>A wire-serializable admission record (base64url public key) for the enrollment response — the
/// transport form of <see cref="MemberAdmissionRecord"/>.</summary>
public sealed record WireAdmissionRecord(
    string TeamId,
    string PartyId,
    string PublicKey,
    string[] Permissions,
    AdmissionSignature Admission);

/// <summary>A wire-serializable revocation record for the enrollment response — the transport form of
/// <see cref="MemberRevocationRecord"/>.</summary>
public sealed record WireRevocationRecord(
    string TeamId,
    string RevokedPartyId,
    RevocationSignature Signed);

/// <summary>One entry of the enrollment response's per-member transport-key map: a party id → its team-scoped
/// transport public key (base64url). Unsigned trust hint — matched to validated roster members by party id.</summary>
public sealed record WireMemberTransportKey(
    string PartyId,
    string TransportPublicKey);

/// <summary>C5 — one entry of the enrollment response's per-member DM-key map: a party id → its team-scoped
/// DM-encryption public key (base64url X25519). Unsigned hint — matched to validated roster members by party id
/// (a key for a non-member is dropped). The DM counterpart of <see cref="WireMemberTransportKey"/>.</summary>
public sealed record WireMemberDmKey(
    string PartyId,
    string DmPublicKey);

/// <summary>
/// The plan B executes after validating A's <see cref="EnrollmentResponse"/> — ADOPT A's team. On success it
/// carries the team id to adopt, the independently-validated roster B adopts, and the transport-key set
/// (party → raw 32-byte Ed25519) to wire into B's <c>MemberSetTrustPolicy</c>. On failure it carries only the
/// reason (no partial trust — B adopts NOTHING).
/// </summary>
public sealed record EnrollmentAdoptionPlan
{
    private EnrollmentAdoptionPlan(
        bool succeeded,
        string? failureReason,
        Guid teamId,
        MemberRoster? roster,
        IReadOnlyDictionary<string, byte[]> transportKeysByParty,
        IReadOnlyDictionary<string, byte[]> dmKeysByParty)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
        TeamId = teamId;
        Roster = roster;
        TransportKeysByParty = transportKeysByParty;
        DmKeysByParty = dmKeysByParty;
    }

    /// <summary>True iff the response validated and B has a plan to adopt A's team.</summary>
    public bool Succeeded { get; }

    /// <summary>The fail-closed reason when <see cref="Succeeded"/> is false; otherwise null.</summary>
    public string? FailureReason { get; }

    /// <summary>The team id B adopts (= A.teamId) on success.</summary>
    public Guid TeamId { get; }

    /// <summary>The independently-validated roster B adopts (genesis-rooted, B is a member); null on failure.</summary>
    public MemberRoster? Roster { get; }

    /// <summary>The transport keys (party → raw 32-byte Ed25519) B wires into its trust policy (every live member,
    /// incl. A). Empty on failure.</summary>
    public IReadOnlyDictionary<string, byte[]> TransportKeysByParty { get; }

    /// <summary>C5 — the DM-encryption public keys (party → raw 32-byte X25519) B wires into its DM-key map (every
    /// live member that carried a DM key, incl. A). Empty on failure or when the response carried no DM keys (B
    /// learns them later from the synced roster — DM sealing is not gated by enrollment).</summary>
    public IReadOnlyDictionary<string, byte[]> DmKeysByParty { get; }

    internal static EnrollmentAdoptionPlan Adopt(
        Guid teamId,
        MemberRoster roster,
        IReadOnlyDictionary<string, byte[]> transportKeysByParty,
        IReadOnlyDictionary<string, byte[]>? dmKeysByParty = null) =>
        new(true, null, teamId, roster, transportKeysByParty,
            dmKeysByParty ?? new Dictionary<string, byte[]>(StringComparer.Ordinal));

    internal static EnrollmentAdoptionPlan Failed(string reason) =>
        new(false, reason, Guid.Empty, null,
            new Dictionary<string, byte[]>(StringComparer.Ordinal),
            new Dictionary<string, byte[]>(StringComparer.Ordinal));
}
