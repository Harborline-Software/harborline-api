using System;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// The peer-to-peer ADMISSION protocol coordinator (enrollment Phase B; cerebrum [2026-06-20] "admission
/// mechanism = peer-to-peer (proximity/QR + invite); Bridge-mediated DEFERRED"). It is the single seam the two
/// confirmed admission MODES drive through — proximity/QR (same-office, MITM-resistant) and invite-code (remote,
/// single-use/short-TTL) — both ending in the SAME Phase A operation: an in-roster admin signs the joining
/// (party, key) pair into the <see cref="MemberRoster"/>. The Harborline App QR-scan / invite-entry UI is a flagged FED
/// follow-on; this is the protocol + a thin seam it calls, not the UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two modes share a security spine.</b> Whatever the channel, the admit always reduces to the admitting
/// admin SIGNING the exact (joining party, joining pubkey) pair (Phase A <see cref="MemberRoster.Admit"/>). That
/// signature — over the binding, by an admin who holds <c>members:admit</c>, subset-bounded by no-escalation — is
/// what makes the roster forge-proof. The modes differ ONLY in how the joining pubkey reaches the admin:
/// </para>
/// <list type="bullet">
///   <item><b>Proximity/QR (<see cref="AdmitOverProximity"/>):</b> the admin's device shows a QR carrying the
///     <see cref="TeamTrustAnchor"/>; the new device (with its freshly-generated keypair) returns its (party,
///     pubkey) over the SAME in-person channel; the admin signs it in. <b>MITM-resistance:</b> the in-person
///     channel binds the RIGHT pubkey to the RIGHT person — a channel attacker who substitutes a pubkey causes
///     the admin to sign the WRONG key, and the substituted member can then never produce a forge-proof-valid
///     message as the intended party (their key ≠ what the victim would sign with). The admin physically
///     confirming the device closes the substitution gap. Mutual-QR (both devices show + scan) is strongest.</item>
///   <item><b>Invite-code (<see cref="AdmitOverInvite"/>):</b> the admin mints a single-use, short-TTL
///     <see cref="AdmissionToken"/> (carrying the anchor), delivered out-of-band; the new device presents the
///     token + its pubkey. The token is REDEEMED single-use (replay/expiry rejected by the
///     <see cref="IAdmissionTokenStore"/>) and the joiner gets the <b>minimal (member) composition by
///     default</b> until an admin elevates them. The bearer-credential weakness is bounded by those three.</item>
/// </list>
/// <para>
/// <b>No-escalation is inherited, not re-checked here.</b> <see cref="MemberRoster.Admit"/> enforces the joining
/// set ⊆ the admitter's held set; this coordinator never bypasses it. The invite mode defaults the joiner to the
/// member composition; the proximity mode lets the admin choose the granted set (still subset-bounded by Admit).
/// </para>
/// </remarks>
public sealed class AdmissionCoordinator
{
    private readonly IOperationVerifier _verifier;
    private readonly IAdmissionTokenStore _tokens;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Construct over the signature verifier (re-verifies the admin's admission signature, via Admit), the
    /// single-use invite token store, and a clock (testable; defaults to <see cref="TimeProvider.System"/>).
    /// </summary>
    public AdmissionCoordinator(
        IOperationVerifier verifier,
        IAdmissionTokenStore tokens,
        TimeProvider? clock = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Mint a single-use, short-TTL invite for <paramref name="anchor"/>'s team and record it in the token store
    /// so it can be redeemed exactly once. The admin delivers the returned token out-of-band (the Harborline App UI's
    /// job — FED follow-on).
    /// </summary>
    public AdmissionToken CreateInvite(TeamTrustAnchor anchor, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var token = AdmissionToken.Mint(anchor, _clock.GetUtcNow(), ttl);
        _tokens.Issue(token);
        return token;
    }

    /// <summary>
    /// PROXIMITY/QR admission. The admin (an in-roster member holding <c>members:admit</c>) signs the joining
    /// device's (party, pubkey) — captured over the in-person channel — into the roster with the chosen granted
    /// set. Returns the NEW roster. MITM-resistance is the in-person channel's property (the admin confirms the
    /// device); the cryptographic binding is the Phase A admit signature over the exact (party, key) pair.
    /// </summary>
    /// <param name="roster">The current team roster.</param>
    /// <param name="admitterPartyId">The admitting admin's party id.</param>
    /// <param name="admitterSigner">The admitting admin's signer (its key must match the admin's roster binding).</param>
    /// <param name="joiningPartyId">The new member's party id.</param>
    /// <param name="joiningPublicKey">The new device's freshly-generated public key, captured in-person.</param>
    /// <param name="grantedPermissions">The set to grant (subset-bounded by no-escalation in Admit).</param>
    /// <param name="joiningDmPublicKey">C5 — the joining device's team-scoped DM-encryption PUBLIC key (base64url
    /// X25519), captured over the SAME in-person channel as the principal key. Bound INTO the signed admission so the
    /// (party → DM-pubkey) binding is forge-proof (the DM key-substitution fix). Empty = a joiner that presents no DM
    /// key.</param>
    /// <param name="joiningXWingPublicKey">C5-X — the joining device's team-scoped X-Wing (X25519 + ML-KEM-768) PUBLIC
    /// key (base64url, 1216-byte raw), captured over the SAME in-person channel. Bound INTO the signed admission so the
    /// (party → X-Wing-pubkey) binding is forge-proof (the X-Wing key-substitution DEK-leak fix, #1489 — an X-Wing key
    /// is a confidentiality key like the DM key). Empty = a joiner that presents no X-Wing key (X-Wing-incapable → suite
    /// #1).</param>
    public MemberRoster AdmitOverProximity(
        MemberRoster roster,
        string admitterPartyId,
        IOperationSigner admitterSigner,
        string joiningPartyId,
        PrincipalId joiningPublicKey,
        PermissionSet grantedPermissions,
        string joiningDmPublicKey = "",
        string joiningXWingPublicKey = "")
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(grantedPermissions);

        var now = _clock.GetUtcNow();
        // The admit signature is over the EXACT (party, key, DM-key, X-Wing-key) tuple the admin captured in person —
        // this is what makes a channel-substituted pubkey a self-defeating attack (the admin signs the wrong key; the
        // substituted member can never forge-prove as the intended party, and its substituted DM/X-Wing key never
        // validates). No-escalation + members:admit + signer-key-matches-admitter are all enforced inside Admit.
        return roster.Admit(
            admitterPartyId: admitterPartyId,
            admitterSigner: admitterSigner,
            newPartyId: joiningPartyId,
            newPublicKey: joiningPublicKey,
            grantedPermissions: grantedPermissions,
            verifier: _verifier,
            issuedAt: now,
            nonce: Guid.NewGuid(),
            newDmPublicKey: joiningDmPublicKey ?? string.Empty,
            newXWingPublicKey: joiningXWingPublicKey ?? string.Empty);
    }

    /// <summary>
    /// INVITE-CODE admission. Redeems the bearer <paramref name="tokenId"/> single-use (replay/expiry rejected),
    /// then an in-roster admin signs the joining (party, pubkey) into the roster with the MINIMAL (member)
    /// composition by default. Returns an <see cref="InviteAdmissionResult"/> — the new roster on success, or the
    /// rejection reason (fail-closed) when the token is unknown / replayed / expired.
    /// </summary>
    /// <param name="roster">The current team roster.</param>
    /// <param name="tokenId">The invite token id the joiner presents.</param>
    /// <param name="admitterPartyId">The in-roster admin who signs the admission (typically the inviter).</param>
    /// <param name="admitterSigner">The admitting admin's signer.</param>
    /// <param name="joiningPartyId">The new member's party id.</param>
    /// <param name="joiningPublicKey">The new device's public key, presented with the token.</param>
    /// <param name="joiningDmPublicKey">C5 — the new device's team-scoped DM-encryption PUBLIC key (base64url
    /// X25519), presented with the token + bound into B's signed enrollment request (proof-of-possession). The
    /// admitter binds it INTO the signed admission, so the (party → DM-pubkey) binding is forge-proof (the DM
    /// key-substitution fix). Empty = a legacy joiner that presents no DM key.</param>
    /// <param name="joiningXWingPublicKey">C5-X — the new device's team-scoped X-Wing PUBLIC key (base64url, 1216-byte
    /// raw), presented with the token + bound into B's signed enrollment request. The admitter binds it INTO the signed
    /// admission, so the (party → X-Wing-pubkey) binding is forge-proof (the X-Wing key-substitution DEK-leak fix,
    /// #1489). Empty = a legacy joiner that presents no X-Wing key (X-Wing-incapable → suite #1).</param>
    /// <param name="admittedUnderSessionEvidence">#3167 R1.2 — on the web-admitted PAIRING path, the opaque
    /// mint-time SessionCorrelationId, bound INTO the signed admission as the (soft) minting-session AUDIT evidence.
    /// Empty (default) = no session-evidence field signed. This is a SEPARATE axis from the token-id binding (see
    /// <paramref name="bindAdmittedViaTokenId"/>) — a change from the earlier coupling where the token-id binding
    /// rode on this field's non-emptiness (verdict F1).</param>
    /// <param name="bindAdmittedViaTokenId">#3167 R1.2 (verdict F1 — decoupled) — when true (the web-admitted
    /// PAIRING path), the redeemed <paramref name="tokenId"/> is bound INTO the signed admission UNCONDITIONALLY, so
    /// the STRUCTURAL "admitted by token T" provenance ("an admission whose signature does not bind the token
    /// identity is invalid on this path") does NOT ride on the soft session-evidence audit field being present. The
    /// plain invite path passes false (default) → no token-id bound → byte-stable legacy admission.</param>
    /// <param name="grantedPermissions">Explicit PBAC bundle to carry into the signed roster admission.</param>
    public InviteAdmissionResult AdmitOverInvite(
        MemberRoster roster,
        string tokenId,
        string admitterPartyId,
        IOperationSigner admitterSigner,
        string joiningPartyId,
        PrincipalId joiningPublicKey,
        PermissionSet grantedPermissions,
        string joiningDmPublicKey = "",
        string joiningXWingPublicKey = "",
        string admittedUnderSessionEvidence = "",
        bool bindAdmittedViaTokenId = false)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(grantedPermissions);

        var redemption = RedeemForAdmission(tokenId);
        if (!redemption.Accepted)
        {
            // Fail-closed: a bad token never admits anyone, and never reaches the admit signature.
            return InviteAdmissionResult.Rejected(redemption.Outcome);
        }

        return AdmitRedeemedInvite(
            roster,
            redemption.Receipt!,
            admitterPartyId,
            admitterSigner,
            joiningPartyId,
            joiningPublicKey,
            grantedPermissions,
            joiningDmPublicKey,
            joiningXWingPublicKey,
            admittedUnderSessionEvidence,
            bindAdmittedViaTokenId);
    }

    /// <summary>
    /// Atomically redeem one invite and return the structural receipt required by
    /// <see cref="AdmitRedeemedInvite"/>. A rejected store result never produces a receipt.
    /// </summary>
    public AdmissionRedemptionResult RedeemForAdmission(string tokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        var redeem = _tokens.Redeem(tokenId, _clock.GetUtcNow());
        return redeem.Accepted && redeem.Token is not null
            ? new AdmissionRedemptionResult(
                RedeemOutcome.Accepted,
                new AdmissionRedemptionReceipt(redeem.Token))
            : new AdmissionRedemptionResult(redeem.Outcome, null);
    }

    /// <summary>
    /// Sign an invite admission only after the caller presents the receipt created by a successful token-store
    /// redemption. This is the token-gated signer seam used by web pairing; a token id alone cannot reach it.
    /// </summary>
    public InviteAdmissionResult AdmitRedeemedInvite(
        MemberRoster roster,
        AdmissionRedemptionReceipt redemptionReceipt,
        string admitterPartyId,
        IOperationSigner admitterSigner,
        string joiningPartyId,
        PrincipalId joiningPublicKey,
        PermissionSet grantedPermissions,
        string joiningDmPublicKey = "",
        string joiningXWingPublicKey = "",
        string admittedUnderSessionEvidence = "",
        bool bindAdmittedViaTokenId = false)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(redemptionReceipt);
        ArgumentNullException.ThrowIfNull(grantedPermissions);

        var tokenId = redemptionReceipt.TokenId;
        if (TeamTrustAnchor.FromRoster(roster) != redemptionReceipt.Anchor)
        {
            return InviteAdmissionResult.Rejected(
                redemptionReceipt,
                InviteAdmissionResult.TokenTeamMismatch);
        }
        var now = _clock.GetUtcNow();

        // The explicit invitation bundle is bounded by Admit's no-escalation guard. C5 — the joiner's DM key
        // (proof-of-possession-verified on the enroll request) is signed into the admission too.
        var newRoster = roster.Admit(
            admitterPartyId: admitterPartyId,
            admitterSigner: admitterSigner,
            newPartyId: joiningPartyId,
            newPublicKey: joiningPublicKey,
            grantedPermissions: grantedPermissions,
            verifier: _verifier,
            issuedAt: now,
            nonce: Guid.NewGuid(),
            newDmPublicKey: joiningDmPublicKey ?? string.Empty,
            newXWingPublicKey: joiningXWingPublicKey ?? string.Empty,
            // #3167 R1.2 (verdict F1 — decoupled) — the token-id binding is the STRUCTURAL pairing-path property, so
            // it rides on an EXPLICIT bindAdmittedViaTokenId flag (the pairing path passes true), NOT on the soft
            // session-evidence field being non-empty. The token id bound HERE is the same one just redeemed
            // (admitter authority derived from THIS redeemed token, never standing). The plain invite path passes
            // false → no token id → byte-stable legacy admission.
            admittedViaTokenId: bindAdmittedViaTokenId ? tokenId : string.Empty,
            admittedUnderSessionEvidence: admittedUnderSessionEvidence ?? string.Empty);

        return InviteAdmissionResult.Admitted(newRoster, redemptionReceipt);
    }
}

/// <summary>The outcome of an invite-code admission: the new roster on success, or the rejection reason.</summary>
/// <param name="Outcome">The redemption outcome (<see cref="RedeemOutcome.Accepted"/> on success).</param>
/// <param name="Roster">The new roster when admitted; otherwise null.</param>
/// <param name="Decision">The one redeemed-token decision carried through signing; null only before redemption.</param>
/// <param name="RefusalCode">Stable internal refusal code when a redeemed decision is refused at commit.</param>
public sealed record InviteAdmissionResult(
    RedeemOutcome Outcome,
    MemberRoster? Roster,
    AdmissionRedemptionReceipt? Decision = null,
    string? RefusalCode = null)
{
    /// <summary>Stable internal refusal code for a redeemed token presented to a different team roster.</summary>
    public const string TokenTeamMismatch = "token_team_mismatch";

    /// <summary>True iff the invite was redeemed and the member admitted.</summary>
    public bool Admitted_ => Outcome == RedeemOutcome.Accepted && Roster is not null;

    /// <summary>True when no token redemption decision existed for this outcome.</summary>
    public bool PreDecision => Decision is null;

    internal static InviteAdmissionResult Admitted(
        MemberRoster roster,
        AdmissionRedemptionReceipt decision) =>
        new(RedeemOutcome.Accepted, roster, decision);

    internal static InviteAdmissionResult Rejected(RedeemOutcome outcome) =>
        new(outcome, null);

    internal static InviteAdmissionResult Rejected(
        AdmissionRedemptionReceipt decision,
        string refusalCode) =>
        new(RedeemOutcome.Accepted, null, decision, refusalCode);
}
