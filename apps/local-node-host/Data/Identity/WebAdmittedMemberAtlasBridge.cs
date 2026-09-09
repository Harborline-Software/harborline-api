using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// MTW-2 #3107 — the FIRST-WIRE-ENROLLMENT ATLAS BRIDGE. A member admitted through the web
/// invitation flow (#2614) exists on the web tenant-identity plane — an account, a live Party
/// binding, a live grant + authorization epoch, and an <see cref="TenantMembershipStatus.Active"/>
/// membership record — but is NOT yet present in the signed atlas <see cref="MemberRoster"/>
/// (the atlas admission is deferred out of web-acceptance per Option A,
/// admiral-ruling-2026-07-23T0045Z; see <see cref="LiveTenantMembershipAuthorityAdmission"/>).
/// This bridge is the deferred step: when that member's node first connects and performs two-sided
/// wire enrollment, it records the signed, genesis-rooted roster admission — but ONLY after
/// re-verifying the web-plane membership pins <b>against the identity presented at enrollment</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is attested (enrollment integrity, NOT impersonation-resistance).</b> The bridge does not
/// claim to stop a determined impersonator; it attests that the four web-plane membership pins agree
/// with the enrollment before the admitter signs atlas presence, so a roster admission is never
/// signed for an enrollment whose (tenant, principal, party, grant) does not match a live, Active web
/// membership. Every pin is RE-READ live (the persisted membership snapshot carries only the
/// coordinates); a drifted, revoked, or missing grant refuses exactly as the web-plane fence refuses.
/// </para>
/// <para>
/// <b>The four pins, verified against the enrollment (any mismatch ⇒ NO admission).</b>
/// <list type="number">
///   <item><b>tenant</b> — the membership tenant resolves a live Party binding under that exact tenant.</item>
///   <item><b>PrincipalUserId</b> — the binding is for the membership's canonical principal.</item>
///   <item><b>PartyId</b> — ticket 294 slice 2a: the binding's canonical tenant PRINCIPAL id equals the
///     party id the enrollment presents (<paramref name="enrollmentPartyId"/>), because that principal
///     IS the one party key the roster edge and the grant store share. This is what ties the enrolling
///     identity to the web-plane member; a wire enrollment claiming a different party — including one
///     presenting the old People PartyId key space — is refused with <c>party_binding_mismatch</c>.</item>
///   <item><b>grant identity</b> — a live grant (matching owner-version, not revoked, inside its
///     validity window) plus the current authorization epoch exists for (tenant, principal). This
///     mirrors the mandatory grant/epoch teeth of <see cref="LiveTenantMembershipAuthorityAdmission"/>
///     so grant revocation remains the sufficient revocation lever.</item>
/// </list>
/// </para>
/// <para>
/// <b>Composition, not a new authority (bounds of #3107).</b> On a clean pin verification the bridge
/// signs the admission through the EXISTING <see cref="AdmissionCoordinator.AdmitOverInvite"/> —
/// admitter present and signing, single-use invite redeemed, no-escalation enforced inside
/// <see cref="MemberRoster.Admit"/>. The bridge introduces no standing tenant-scoped admitter
/// authority for the node and pre-signs nothing at invitation-issuance time; it is a read-only
/// verification over web-plane state that GATES the already-reviewed signed-admission path.
/// </para>
/// </remarks>
internal sealed class WebAdmittedMemberAtlasBridge
{
    private readonly ICanonicalPrincipalPartyReader _partyReader;
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _grantFactory;
    private readonly AdmissionCoordinator _coordinator;
    private readonly IWebPairingInviteBindingStore _pairingBindings;
    private readonly TimeProvider _timeProvider;
    private readonly IAuthorizationClosureReader _authorization;

    public WebAdmittedMemberAtlasBridge(
        ICanonicalPrincipalPartyReader partyReader,
        IDbContextFactory<NodeLocalSearchDbContext> grantFactory,
        AdmissionCoordinator coordinator,
        IWebPairingInviteBindingStore pairingBindings,
        TimeProvider timeProvider,
        IAuthorizationClosureReader authorization)
    {
        _partyReader = partyReader ?? throw new ArgumentNullException(nameof(partyReader));
        _grantFactory = grantFactory ?? throw new ArgumentNullException(nameof(grantFactory));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _pairingBindings = pairingBindings ?? throw new ArgumentNullException(nameof(pairingBindings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    }

    /// <summary>
    /// The admission's grant conferral, through the ONE derivation the 3a boot backfill also uses. The
    /// configuration store is built over the grant factory this bridge already holds — no new constructor
    /// seam, and every existing composition site keeps working. The role vocabulary is empty on purpose:
    /// <c>StageAdmissionGrantAsync</c> always supplies the admission's own per-admission vocabulary, so the
    /// constructor's reader is never consulted on this path.
    /// ponytail: an empty vocabulary reader here, inject the composed one if any other member of the store
    /// is ever called from this bridge.
    /// </summary>
    private Task<AccessGrant?> ConferAdmissionGrantAsync(
        TenantId tenant, string admittedPartyId, string admitterPartyId, PermissionSet permissions,
        CancellationToken cancellationToken) =>
        new NodeEfAuthorizationConfigurationStore(_grantFactory, new InMemoryRoleVocabulary([]))
            .ConferAdmissionGrantAsync(
                tenant, admittedPartyId, admitterPartyId, permissions,
                _timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>
    /// The #3107 FRONT DOOR — admit a web-admitted member's first device from the single-use device-pairing
    /// token minted under their authenticated web session (<see cref="WebAdmittedMemberPairingTokenMint"/>).
    /// Looks up the four web-plane pins bound to <paramref name="pairingTokenId"/> at mint time, requires the
    /// enrollment to present the exact party the token was minted for, then RE-VERIFIES those pins against
    /// live web-plane state and the enrollment before signing (via
    /// <see cref="AdmitOnFirstEnrollmentAsync"/>). Fail-closed at every step — an unknown token, a party the
    /// token was not minted for, any drifted/revoked pin, or a replayed/expired token all refuse and sign
    /// NOTHING.
    /// </summary>
    /// <remarks>
    /// This is the ruling's re-scope of the bridge: its front door is the pairing token, not a caller-passed
    /// <see cref="TenantMembershipSnapshot"/>. The bound pins carry the provenance of the member's own
    /// authenticated web session; the verifier half (<see cref="AdmitOnFirstEnrollmentAsync"/>) is composed
    /// unchanged.
    /// </remarks>
    /// <param name="roster">The current signed team roster the admitter holds.</param>
    /// <param name="admitterPartyId">The in-roster admin who signs the admission (the founder/inviter).</param>
    /// <param name="admitterSigner">The admitting admin's signer.</param>
    /// <param name="pairingTokenId">The single-use pairing token the enrolling device presents.</param>
    /// <param name="enrollmentPartyId">The party id the enrolling node presents — ticket 294 slice 2a: the
    /// joiner's CANONICAL TENANT PRINCIPAL id, the key the token was minted for.</param>
    /// <param name="enrollmentPrincipalKey">The atlas principal public key the enrolling node presents.</param>
    /// <param name="joiningDmPublicKey">F2 (C5) — the enrolling device's team-scoped DM-encryption PUBLIC key
    /// (base64url X25519), presented at enrollment + proof-of-possession-bound in the enrollment request. Threaded
    /// INTO the signed admission so the first (one-shot, first-write-wins) admission carries the forge-proof
    /// (party → DM-pubkey) binding peers need (#1489). Empty = a device that presents no DM key.</param>
    /// <param name="joiningXWingPublicKey">F2 (C5-X) — the enrolling device's team-scoped X-Wing (X25519 + ML-KEM-768)
    /// PUBLIC key (base64url, 1216-byte raw). Threaded INTO the signed admission so the first admission carries the
    /// forge-proof (party → X-Wing-pubkey) confidentiality binding (#1489 DEK-leak fix). Empty = an X-Wing-incapable
    /// device (→ suite #1).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<AtlasAdmissionOutcome> AdmitFromPairingTokenAsync(
        MemberRoster roster,
        string admitterPartyId,
        IOperationSigner admitterSigner,
        string pairingTokenId,
        string enrollmentPartyId,
        PrincipalId enrollmentPrincipalKey,
        string joiningDmPublicKey = "",
        string joiningXWingPublicKey = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentException.ThrowIfNullOrWhiteSpace(admitterPartyId);
        ArgumentNullException.ThrowIfNull(admitterSigner);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingTokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentPartyId);

        // No pairing was minted for this token under any web session — nothing to admit. Fail-closed.
        var binding = _pairingBindings.Lookup(pairingTokenId);
        if (binding is null)
        {
            return AtlasAdmissionOutcome.Refuse("pairing_binding_unknown");
        }

        return await AdmitFromPairingDecisionAsync(
            roster,
            admitterPartyId,
            admitterSigner,
            binding,
            enrollmentPartyId,
            enrollmentPrincipalKey,
            joiningDmPublicKey,
            joiningXWingPublicKey,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Continue from the exact token binding selected by dispatch; the receipt remains authoritative.</summary>
    internal async Task<AtlasAdmissionOutcome> AdmitFromPairingDecisionAsync(
        MemberRoster roster,
        string admitterPartyId,
        IOperationSigner admitterSigner,
        WebPairingInviteBinding binding,
        string enrollmentPartyId,
        PrincipalId enrollmentPrincipalKey,
        string joiningDmPublicKey = "",
        string joiningXWingPublicKey = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentException.ThrowIfNullOrWhiteSpace(admitterPartyId);
        ArgumentNullException.ThrowIfNull(admitterSigner);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentPartyId);

        // The token was minted for exactly one party under the member's authenticated web session. An
        // enrollment presenting a DIFFERENT party is redeeming a token that was never bound to it — refuse,
        // sign nothing. (This is checked BEFORE the live-state verify + single-use redemption, so a mismatch
        // never consumes the token.)
        if (!string.Equals(binding.BoundPartyId, enrollmentPartyId, StringComparison.Ordinal))
        {
            return AtlasAdmissionOutcome.Refuse("pairing_pin_mismatch");
        }

        // The bound pins carry the provenance of the member's own web session; re-verify them against the
        // LIVE web-plane state + the enrollment, then admit through the existing signed path. The enrolling
        // device's DM + X-Wing confidentiality keys (F2) ride through to the signed admission.
        return await AdmitOnFirstEnrollmentAsync(
            roster,
            admitterPartyId,
            admitterSigner,
            binding.TokenId,
            binding.Membership,
            enrollmentPartyId,
            enrollmentPrincipalKey,
            joiningDmPublicKey,
            joiningXWingPublicKey,
            // #3167 R1.2 — carry the mint-session evidence bound at mint so the signed admission records it.
            binding.SessionCorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verify the web-plane membership pins against the enrollment and, on a clean match, record the
    /// signed, genesis-rooted roster admission via <see cref="AdmissionCoordinator.AdmitOverInvite"/>.
    /// Fail-closed: any pin mismatch (inactive membership, missing/tenant-/principal-/party-mismatched
    /// binding, drifted/revoked/expired grant, stale epoch) returns
    /// <see cref="AtlasAdmissionOutcome.Refuse"/> and signs NOTHING. A rejected invite (replay / expiry
    /// / unknown token) is likewise a fail-closed refusal — the pins verified, but the enrollment
    /// carried no redeemable invite.
    /// </summary>
    /// <param name="roster">The current signed team roster the admitter holds.</param>
    /// <param name="admitterPartyId">The in-roster admin who signs the admission (the founder/inviter).</param>
    /// <param name="admitterSigner">The admitting admin's signer (its key must match its roster binding).</param>
    /// <param name="inviteTokenId">The single-use wire invite the enrolling node presents.</param>
    /// <param name="membership">The web-plane membership pins (tenant, principal, grant, status).</param>
    /// <param name="enrollmentPartyId">The party id the enrolling node presents (JoiningPartyId). Ticket 294
    /// slice 2a: this is the joiner's CANONICAL TENANT PRINCIPAL id, not the People PartyId.</param>
    /// <param name="enrollmentPrincipalKey">The atlas principal public key the enrolling node presents.</param>
    /// <param name="joiningDmPublicKey">F2 (C5) — the enrolling device's team-scoped DM PUBLIC key (base64url
    /// X25519), bound INTO the signed admission. Empty = a device that presents no DM key.</param>
    /// <param name="joiningXWingPublicKey">F2 (C5-X) — the enrolling device's team-scoped X-Wing PUBLIC key
    /// (base64url, 1216-byte raw), bound INTO the signed admission (#1489 DEK-leak fix). Empty = X-Wing-incapable.</param>
    /// <param name="sessionEvidence">#3167 R1.2 — the opaque mint-time SessionCorrelationId; when non-empty it is
    /// bound INTO the signed admission (with the token id) as the minting-session evidence. Empty (default) for the
    /// direct/test callers that do not carry web-session provenance.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<AtlasAdmissionOutcome> AdmitOnFirstEnrollmentAsync(
        MemberRoster roster,
        string admitterPartyId,
        IOperationSigner admitterSigner,
        string inviteTokenId,
        TenantMembershipSnapshot membership,
        string enrollmentPartyId,
        PrincipalId enrollmentPrincipalKey,
        string joiningDmPublicKey = "",
        string joiningXWingPublicKey = "",
        string sessionEvidence = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentException.ThrowIfNullOrWhiteSpace(admitterPartyId);
        ArgumentNullException.ThrowIfNull(admitterSigner);
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteTokenId);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentPartyId);

        // Pin 0 — the membership must be Active. A revoked (or otherwise non-Active) web membership
        // never earns atlas presence.
        if (membership.Status != TenantMembershipStatus.Active)
        {
            return AtlasAdmissionOutcome.Refuse("membership_not_active");
        }

        var tenant = new TenantId(Guid.Parse(membership.TenantId).ToString("D"));
        var principal = new PrincipalUserId(membership.CanonicalPrincipalId);

        // Pins 1-3 — tenant + principal + party. Re-read the LIVE Party binding and require it to be
        // present, under the exact tenant, for the exact principal, AND for the enrollment to present
        // that principal as its party id.
        //
        // Ticket 294 slice 2a — ONE party key. The roster edge is keyed by the CANONICAL TENANT
        // PRINCIPAL id, which is the key the grant store, the closure reader and the admin surface all
        // read (see NodeGatePrincipal). So the pin compares the enrollment's party id against
        // binding.PrincipalUserId, not the People PartyId: a joiner presenting the old People key space
        // refuses here, with the same named reason. The non-null binding still has teeth — it is what
        // proves a live, unambiguous, in-tenant People party exists behind this principal (the reader
        // returns null for missing, ambiguous, tombstoned, detached and wrong-tenant). The People
        // PartyId keeps its own job (attribution stamped on what an act writes); it is no longer an
        // authorization key.
        var binding = await _partyReader.ResolveAsync(tenant, principal, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null ||
            !binding.VerifiedTenant.Equals(tenant) ||
            !binding.PrincipalUserId.Equals(principal) ||
            !string.Equals(binding.PrincipalUserId.Value, enrollmentPartyId, StringComparison.Ordinal))
        {
            return AtlasAdmissionOutcome.Refuse("party_binding_mismatch");
        }

        // Pin 4 — grant identity. A live grant (matching owner-version, not revoked, inside validity)
        // plus the current authorization epoch must exist for (tenant, principal). Mirrors the
        // mandatory grant/epoch teeth of LiveTenantMembershipAuthorityAdmission so the same drift /
        // revocation refuses here.
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var grants = await _grantFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var grant = await grants.Grants.AsNoTracking()
            .Where(LiveWebMembershipGrantQuery.ForTenantAt(tenant, now))
            .SingleOrDefaultAsync(row =>
                row.GrantId == membership.GrantId &&
                row.SubjectId == principal.Value,
            cancellationToken).ConfigureAwait(false);
        var epoch = await grants.GrantAuthorizationEpochs.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == tenant.Value && row.PrincipalId == principal.Value,
            cancellationToken).ConfigureAwait(false);
        if (grant is null ||
            grant.OwnerVersion != membership.GrantOwnerVersion ||
            epoch is null ||
            epoch.AuthorizationEpoch != membership.AuthorizationEpoch)
        {
            return AtlasAdmissionOutcome.Refuse("grant_unavailable");
        }

        var atoms = await _authorization.UserPermissionsAsync(
            tenant, new ActorId(principal.Value), _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        var grantedPermissions = PermissionSet.From(atoms.Atoms
            .Where(atom => atom.Scope.Value == "/")
            .Select(atom => atom.Operation.Value));

        // F3 (fail-closed ordering — token-burn DoS fix). The duplicate-party guard is MOVED AHEAD of the
        // single-use redemption. AdmitOverInvite redeems (consumes) the token BEFORE MemberRoster.Admit runs, and
        // Admit THROWS RosterGuardException when the enrolling party is already a member (the realistic "connect
        // your device TWICE" case — two tokens minted, first enrolls, second is presented after the party is in
        // the roster). Left to AdmitOverInvite, that would BURN the second single-use token on a request that can
        // never succeed. Refusing HERE — before AdmitOverInvite — consumes NOTHING, so the token stays redeemable
        // (no griefing / DoS on the member's own pairing token). Tested: induced Admit failure leaves the token
        // redeemable.
        if (roster.Contains(enrollmentPartyId))
        {
            return AtlasAdmissionOutcome.Refuse("already_member");
        }

        // All four pins agree with the enrollment. Record atlas presence through the EXISTING signed admission
        // path — admitter present and signing, invite redeemed single-use, no-escalation enforced inside Admit.
        // The admitter binds the party the enrollment presents (the same party just pin-verified) to the principal
        // key it presents AND — F2 (#1489 key-substitution fix) — the enrolling device's team-scoped DM + X-Wing
        // confidentiality PUBLIC keys, so the FIRST (one-shot, first-write-wins) admission carries the forge-proof
        // (party → DM/X-Wing) bindings peers need. A device that presents no such key threads empty (→ suite #1),
        // exactly as the existing proximity/invite modes.
        InviteAdmissionResult result;
        try
        {
            // R1.3 — the token id cannot reach the pairing signer directly. Only a successful atomic store
            // redemption yields this assembly-minted receipt; the signing seam below requires the receipt type.
            var redemption = _coordinator.RedeemForAdmission(inviteTokenId);
            if (!redemption.Accepted || redemption.Receipt is null)
            {
                return AtlasAdmissionOutcome.Refuse("invite_rejected");
            }

            result = _coordinator.AdmitRedeemedInvite(
                roster,
                redemption.Receipt,
                admitterPartyId,
                admitterSigner,
                enrollmentPartyId,
                enrollmentPrincipalKey,
                grantedPermissions,
                joiningDmPublicKey ?? string.Empty,
                joiningXWingPublicKey ?? string.Empty,
                // #3167 R1.2 — the mint-session evidence rides as the (soft) audit field.
                admittedUnderSessionEvidence: sessionEvidence ?? string.Empty,
                // #3167 R1.2 (verdict F1 — decoupled) — this bridge IS the web-admitted PAIRING path, so the
                // redeemed token id is bound into the signed admission UNCONDITIONALLY (the structural "admitted by
                // token T" provenance must not ride on the soft session-evidence field being present).
                bindAdmittedViaTokenId: true);
        }
        catch (RosterGuardException guard)
        {
            // A residual Admit guard threw AFTER the pre-check. Honor the fail-closed CONTRACT: a coarse Refuse,
            // NEVER an uncaught exception escaping the bridge as a 500 / stack leak. Preserve a DISTINCT internal
            // audit reason per cause (F4's own keep-distinct-internal premise) — a genuine crypto-integrity
            // failure or a privilege-escalation attempt MUST NOT be audited identically to a fat-fingered admitter
            // key. The WIRE output still collapses all of these to one opaque token (ToWire(), unchanged).
            return AtlasAdmissionOutcome.Refuse(ClassifyAdmitGuard(guard));
        }

        if (!result.Admitted_ || result.Roster is null)
        {
            // A post-redemption refusal keeps the stable internal reason recorded by the SAME receipt decision.
            return AtlasAdmissionOutcome.Refuse(result.RefusalCode ?? "invite_rejected");
        }

        // ADR 0066 clause 3 / ticket 293 slice 4 — THE ADMISSION CONFERS THE ADMITTED PARTY'S GRANT.
        // This is the one place both admission paths (pairing and first enrollment) commit a roster edge, so
        // one conferral here covers both admitters. The atoms are the permission set the admission itself
        // carried; the scope is the install root; the key is the roster party id, which since ticket 294
        // slice 2a is the canonical tenant principal id (see
        // NodeEfAuthorizationConfigurationStore.StageAdmissionGrantAsync for why the gate finds it there).
        // A conferral failure THROWS out of the bridge before the outcome is admitted, so the caller never
        // publishes the roster record: no half state where a party is on the roster with no grant.
        //
        // What it ADDS, precisely (fix 4, D2). At this instant the atoms are a copy of what the principal
        // already holds: grantedPermissions was derived above from this subject's own closure read, and pin 4
        // has just required the web-plane membership grant InitialGrantIssuanceService issued at invitation
        // acceptance to be live — that grant is where those atoms come from. The conferral is NOT therefore
        // a second writer of the same grant: it anchors the set to the SIGNED ADMISSION under its own
        // per-admission role, so the authority survives revocation of the membership grant, which the
        // enrollment itself does not depend on. WebAdmittedMemberAtlasBridgeTests pins exactly that — revoke
        // the membership grant after admitting and the admitted member is still allowed; delete this call and
        // it is refused.
        await ConferAdmissionGrantAsync(tenant, enrollmentPartyId, admitterPartyId, grantedPermissions, cancellationToken)
            .ConfigureAwait(false);
        return AtlasAdmissionOutcome.Admit(result.Roster);
    }

    /// <summary>
    /// Classify a residual <see cref="RosterGuardException"/> from <see cref="MemberRoster.Admit"/> into a
    /// DISTINCT internal audit reason (F4 keeps distinct causes server-side; the wire still collapses via
    /// <see cref="AtlasAdmissionOutcome.ToWire"/>). Crypto-integrity and privilege-escalation causes are the
    /// security-relevant ones that must NOT be flattened into the benign availability fallback.
    /// </summary>
    /// <remarks>
    /// <see cref="RosterGuardException"/> carries only a message (no structured code), so this classifies on
    /// stable, distinctive substrings of <see cref="MemberRoster.Admit"/>'s guard messages. The coupling is
    /// pinned by a test that induces the REAL signature-verify throw — if those messages drift, the test fails
    /// (rather than this silently degrading to the fallback). Unrecognized causes fall back to
    /// <c>admitter_unavailable</c> (fail-safe: still refuses, still opaque on the wire, just coarser in audit).
    /// </remarks>
    private static string ClassifyAdmitGuard(RosterGuardException guard)
    {
        var message = guard.Message ?? string.Empty;

        // Cryptographic-integrity failure — the produced admission signature did not verify (MemberRoster.Admit,
        // "Produced admission signature did not verify"). The highest-severity audit signal here.
        if (message.Contains("signature did not verify", StringComparison.Ordinal))
        {
            return "admitter_signature_invalid";
        }

        // Privilege-escalation attempt — the admitted permission set exceeds the admitter's held set
        // ("No-escalation violated: ...").
        if (message.Contains("No-escalation", StringComparison.Ordinal))
        {
            return "no_escalation_refused";
        }

        // The enrolling party was admitted in the narrow race between the Contains pre-check and the redeem
        // ("Party '...' is already a member"). Distinct from the benign fallback so the race is legible in audit.
        if (message.Contains("is already a member", StringComparison.Ordinal))
        {
            return "already_member_guard";
        }

        // Fallback — a misconfigured / mis-keyed / under-permissioned admitter (not-a-member, signing-key
        // mismatch, no members:admit). A genuine admitter-availability problem, not an attack signal.
        return "admitter_unavailable";
    }

    /// <summary>
    /// The outcome of a first-enrollment atlas admission — the new signed roster on success, or a
    /// coarse fail-closed refusal reason. On refusal NOTHING is signed and the caller's roster is
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// <b>F4 — one opaque wire refusal (no CONTENT enumeration oracle).</b> <see cref="RefusalReason"/> keeps the
    /// DISTINCT internal cause (unknown/mismatch/inactive/drifted/revoked/replayed/already-member/admitter-*) for
    /// SERVER-SIDE audit + logging only. It MUST NOT cross the wire: a prober that could read "unknown token" vs
    /// "valid-token-wrong-party" vs "valid-token-revoked-grant" off the response BODY gets a content oracle. The
    /// wire-facing seam is <see cref="ToWire"/> — it collapses EVERY refusal, whatever its cause, to the single
    /// opaque <see cref="AtlasAdmissionWireOutcome.OpaqueWireRefusal"/>, so all refusal paths are byte-identical on
    /// the wire. This equalizes the CONTENT channel only — the refusal paths still exit after materially different
    /// work (0 vs 2 DB reads vs a signing throw), so TIMING-channel equalization (constant-work / uniform-latency
    /// refusal) is a wiring-layer gate on the enrollment-path follow-on, not closed here.
    /// </remarks>
    internal sealed record AtlasAdmissionOutcome
    {
        private AtlasAdmissionOutcome(bool admitted, MemberRoster? roster, string? refusalReason)
        {
            Admitted = admitted;
            Roster = roster;
            RefusalReason = refusalReason;
        }

        /// <summary>True iff the pins verified and the signed roster admission was recorded.</summary>
        public bool Admitted { get; }

        /// <summary>The new signed, genesis-rooted roster on success; otherwise null.</summary>
        public MemberRoster? Roster { get; }

        /// <summary>
        /// A coarse, PII-free refusal reason on failure; otherwise null. <b>Audit/logging ONLY (F4)</b> — the
        /// distinct cause is kept server-side and NEVER projected to the wire (see <see cref="ToWire"/>).
        /// </summary>
        public string? RefusalReason { get; }

        internal static AtlasAdmissionOutcome Admit(MemberRoster roster) => new(true, roster, null);

        internal static AtlasAdmissionOutcome Refuse(string reason) => new(false, null, reason);

        /// <summary>
        /// F4 — project to the WIRE-FACING outcome. Success carries the new signed roster; ANY refusal — whatever
        /// its internal <see cref="RefusalReason"/> — collapses to the single opaque
        /// <see cref="AtlasAdmissionWireOutcome.OpaqueWireRefusal"/>, so all refusal paths are byte-identical on the
        /// wire. This is the seam the enrollment-path wiring hands to the wire; the distinct reason never crosses
        /// it. Tested: all refusal paths produce byte-identical wire responses.
        /// </summary>
        internal AtlasAdmissionWireOutcome ToWire() =>
            Admitted && Roster is not null
                ? AtlasAdmissionWireOutcome.Accept(Roster)
                : AtlasAdmissionWireOutcome.Reject();
    }

    /// <summary>
    /// F4 — the WIRE-FACING projection of an <see cref="AtlasAdmissionOutcome"/>. Success carries the new signed
    /// roster; EVERY refusal is byte-identical (the single opaque <see cref="OpaqueWireRefusal"/> token), so the
    /// wire response body carries no content enumeration oracle (timing-channel equalization is a wiring-layer
    /// gate, not closed here). Produced ONLY by <see cref="AtlasAdmissionOutcome.ToWire"/>.
    /// </summary>
    /// <param name="Admitted">True iff the admission was signed.</param>
    /// <param name="Roster">The new signed roster on success; otherwise null.</param>
    /// <param name="Refusal">Empty on success; the single opaque <see cref="OpaqueWireRefusal"/> on ANY refusal.</param>
    internal sealed record AtlasAdmissionWireOutcome(bool Admitted, MemberRoster? Roster, string Refusal)
    {
        /// <summary>The single opaque wire-refusal token EVERY refusal collapses to (F4 — no content oracle).</summary>
        internal const string OpaqueWireRefusal = "enrollment_refused";

        internal static AtlasAdmissionWireOutcome Accept(MemberRoster roster) =>
            new(true, roster, string.Empty);

        internal static AtlasAdmissionWireOutcome Reject() =>
            new(false, null, OpaqueWireRefusal);
    }
}
