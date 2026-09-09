using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The JOINER half of the two-sided WIRE ENROLLMENT protocol (cerebrum [2026-06-21] — the half that NEVER
/// existed). It is the B-side runtime that turns a received invite (token + out-of-band team anchor) into MUTUAL
/// trust with the admitter A: it derives B's A-team-scoped transport key, signs + sends the enroll request over
/// the wire, validates A's response against the invite anchor, and ADOPTS A's team into B's local roster + trust
/// map. After it returns success, B's <c>MemberSetTrustPolicy</c> trusts A's wire HELLO and B's own HELLO key is
/// the A-team subkey both nodes expect → the trusted session passes → roster-sync + comms converge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the HKDF derivation lives.</b> B's transport key for A's team is HKDF(B-root-private, A.teamId) —
/// derived HERE (the host has kernel-security's <see cref="ITeamSubkeyDerivation"/>) via
/// <see cref="TeamScopedNodeIdentity.Derive"/>, exactly as the per-team registrar derives it. The pure protocol
/// (<see cref="WireEnrollment"/>, foundation-identity-atlas) only handles already-derived raw keys, so the
/// identity package stays free of kernel-security.
/// </para>
/// <para>
/// <b>Wire transport is injected.</b> The actual POST to A's <c>/admission/redeem</c> is supplied as a
/// <see cref="IEnrollmentTransport"/> so this client is testable WITHOUT HTTP (the integration test wires a
/// transport that calls A's route handler in-process; the host wires an HTTP-backed transport). The client owns
/// the protocol; the transport owns the bytes-on-the-wire.
/// </para>
/// <para>
/// <b>Single-team reference edition.</b> Adopting A's team SUPERSEDES B's own genesis team — B's
/// <see cref="NodeTeamRoster"/> is replaced with A's validated roster + the enrolled-member transport set. The
/// caller is responsible for switching B's ACTIVE TEAM to A.teamId afterwards (so the per-team child container
/// derives B's own A-team subkey as the floor + reads the adopted transport keys); this client returns the
/// adopted team id so the caller can drive that switch. (In the integration test the daemon is built directly on
/// the adopted identity; in the host the team-switch path drives it.)
/// </para>
/// <para>
/// <b>Ticket 294 slice 2a — what <c>JoiningPartyId</c> means on this wire.</b> It is the joiner's
/// CANONICAL TENANT PRINCIPAL id (<c>CanonicalPartyBinding.PrincipalUserId.Value</c>), which is the one
/// party key the signed roster edge, the grant store's <c>AccessGrant.Subject</c> and every closure read
/// share. It is NOT the People <c>PartyId</c>; that reference keeps its own job as the attribution
/// stamped on what an act writes. The field is deliberately NOT renamed — the wire record shape is
/// unchanged and only its meaning moved, which is what the roster wire-format version records.
/// </para>
/// </remarks>
public sealed class NodeWireEnrollmentClient
{
    private readonly NodeIdentity _rootIdentity;
    private readonly ITeamSubkeyDerivation _subkeyDerivation;
    private readonly IXWingSubkeyDerivation _xwingSubkeyDerivation;
    private readonly IOperationSigner _principalSigner;
    private readonly string _selfPartyId;
    private readonly NodeTeamRoster _roster;
    private readonly IOperationVerifier _verifier;
    private readonly IOwnTeamRosterSupersession? _rosterSupersession;
    // 293 s3c: B's own grant view. No permission set rides the wire, so the admitter's members:admit is read
    // from B's local grant store; absent, only A's genesis root may have admitted anyone B adopts.
    private readonly IRosterAuthority? _rosterAuthority;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Construct the joiner client.
    /// </summary>
    /// <param name="rootIdentity">B's install ROOT identity (its private key seeds the HKDF transport derivation).</param>
    /// <param name="subkeyDerivation">The team-subkey HKDF primitive (kernel-security).</param>
    /// <param name="xwingSubkeyDerivation">The team-scoped X-Wing HKDF + KEM expansion. Mandatory so a production
    /// joiner cannot silently omit the pairing path's confidentiality key.</param>
    /// <param name="principalSigner">B's PRINCIPAL signer (the comms/roster author key A binds in the roster).</param>
    /// <param name="selfPartyId">B's own party id (must match the party A admits — the Harborline App supplies it).</param>
    /// <param name="roster">B's install-level <see cref="NodeTeamRoster"/> (replaced on successful adoption).</param>
    /// <param name="verifier">The signature verifier (re-validates A's roster to genesis).</param>
    /// <param name="rosterSupersession">The synced roster doctype's adopt-time self-supersession seam (BLOCKER-2):
    /// on a single-team join B retracts its OWN superseded genesis team's records from the synced doctype — run
    /// BEFORE the in-memory adopt and FAIL-CLOSED (a supersession fault aborts the join retryably, adopting nothing;
    /// #1304 F1) so the dual-genesis injection guard never fail-closes the converged roster and a fault can never
    /// leave a silent dual-genesis poison. OPTIONAL — null in a minimal DI test (no synced roster doctype to
    /// supersede); production wires the <c>RosterCrdtProjection</c>.</param>
    /// <param name="clock">Clock for the request issuance instant (testable; defaults to system).</param>
    public NodeWireEnrollmentClient(
        NodeIdentity rootIdentity,
        ITeamSubkeyDerivation subkeyDerivation,
        IXWingSubkeyDerivation xwingSubkeyDerivation,
        IOperationSigner principalSigner,
        string selfPartyId,
        NodeTeamRoster roster,
        IOperationVerifier verifier,
        IOwnTeamRosterSupersession? rosterSupersession = null,
        TimeProvider? clock = null,
        IRosterAuthority? rosterAuthority = null)
    {
        _rootIdentity = rootIdentity ?? throw new ArgumentNullException(nameof(rootIdentity));
        _subkeyDerivation = subkeyDerivation ?? throw new ArgumentNullException(nameof(subkeyDerivation));
        _xwingSubkeyDerivation = xwingSubkeyDerivation
            ?? throw new ArgumentNullException(nameof(xwingSubkeyDerivation));
        _principalSigner = principalSigner ?? throw new ArgumentNullException(nameof(principalSigner));
        ArgumentException.ThrowIfNullOrWhiteSpace(selfPartyId);
        _selfPartyId = selfPartyId;
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _rosterSupersession = rosterSupersession;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _rosterAuthority = rosterAuthority;
    }

    /// <summary>
    /// Derive B's TEAM-SCOPED transport public key for the team named by <paramref name="teamId"/> —
    /// HKDF(B-root, teamId). This is the key B presents in the sync HELLO once it adopts that team, and the key it
    /// sends in the enroll request so A records the RIGHT key (the #1296-F2 lesson: the key must be scoped to the
    /// JOINED team, not B's own genesis team).
    /// </summary>
    public byte[] DeriveTransportPublicKeyForTeam(string teamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        var teamIdentity = TeamScopedNodeIdentity.Derive(_rootIdentity, teamId, _subkeyDerivation);
        return (byte[])teamIdentity.PublicKey.Clone();
    }

    /// <summary>
    /// C5 — derive B's TEAM-SCOPED DM-encryption PUBLIC key for <paramref name="teamId"/> — the X25519 public half
    /// of HKDF(B-root, teamId) over the DM domain (<see cref="NodeDmKeyDerivation"/>). B presents this in the enroll
    /// request so A records the RIGHT DM key (bound into B's signed proof-of-possession), and so the team can derive
    /// per-conversation DM seal keys with B. The PRIVATE half stays node-secret (re-derived in B's
    /// RosterDmKeyResolver from the same root). The IKM is B's root private key — the SAME material the transport
    /// subkey uses (the Ed25519 root private key IS the 32-byte root seed).
    /// </summary>
    public byte[] DeriveDmPublicKeyForTeam(string teamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        return NodeDmKeyDerivation.DeriveDmPublicKey(_rootIdentity.PrivateKey, teamId);
    }

    /// <summary>
    /// Derive B's TEAM-SCOPED X-Wing public key for <paramref name="teamId"/> from the same install root. The
    /// private seed remains derived-on-demand inside kernel-security; only this 1216-byte public half crosses the
    /// enrollment wire and is bound by the joiner's principal signature.
    /// </summary>
    public byte[] DeriveXWingPublicKeyForTeam(string teamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        return _xwingSubkeyDerivation.DeriveXWingPublicKey(_rootIdentity.PrivateKey, teamId);
    }

    /// <summary>
    /// ENROLL over the wire: derive B's A-team-scoped transport key (from the invite anchor's team id), build +
    /// sign the enroll request, send it via <paramref name="transport"/>, validate A's response against the
    /// invite anchor, then SUPERSEDE B's own superseded-team records from the synced roster doctype (fail-closed),
    /// and — only after that supersession succeeds — ADOPT A's team into B's roster + trust map. Returns an
    /// <see cref="EnrollmentOutcome"/>: the adopted team id + B's A-team transport key on success, or the
    /// fail-closed reason (incl. <c>roster_supersession_failed</c> if the synced-doctype purge threw). Adopts
    /// NOTHING on ANY failure — including a supersession fault — so there is no partial trust and no silent
    /// dual-genesis poison (#1304 F1): the join is atomic-ish (fully joins or cleanly does not) and a fault is
    /// always retryable from the unchanged own-team state.
    /// </summary>
    /// <param name="tokenId">The invite token id B presents.</param>
    /// <param name="inviteAnchor">The team anchor B received OUT-OF-BAND with the invite (the trust pin + the
    /// source of A.teamId B scopes its transport key to).</param>
    /// <param name="transport">The wire transport that POSTs the request to A and returns A's response.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<EnrollmentOutcome> EnrollAsync(
        string tokenId,
        TeamTrustAnchor inviteAnchor,
        IEnrollmentTransport transport,
        CancellationToken ct)
        => await EnrollAsync(tokenId, _selfPartyId, inviteAnchor, transport, ct).ConfigureAwait(false);

    /// <summary>Enroll using the Party selected by the admitted local join operation.</summary>
    public async Task<EnrollmentOutcome> EnrollAsync(
        string tokenId,
        string joiningPartyId,
        TeamTrustAnchor inviteAnchor,
        IEnrollmentTransport transport,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joiningPartyId);
        ArgumentNullException.ThrowIfNull(inviteAnchor);
        ArgumentNullException.ThrowIfNull(transport);

        // 1) Derive B's transport key SCOPED TO A's team (from the anchor) — the key B will present on the HELLO.
        var myTransportKey = DeriveTransportPublicKeyForTeam(inviteAnchor.TeamId);
        // C5 — derive B's A-team-scoped DM public key too (the published half a DM peer runs the ECDH against).
        var myDmKey = DeriveDmPublicKeyForTeam(inviteAnchor.TeamId);
        // R3 — the web-pairing path requires B's A-team-scoped X-Wing public key. Its private seed never leaves B.
        var myXWingKey = DeriveXWingPublicKeyForTeam(inviteAnchor.TeamId);

        // 2) Build + sign the enroll request. The principal signature covers token id + DM + X-Wing key bytes,
        // which is the R4-a binding that prevents an on-path caller from substituting either confidentiality key.
        // ONE instant for this enrollment: the request is signed at it and B's grant authority is read at it.
        var at = _clock.GetUtcNow();
        var request = WireEnrollment.BuildRequest(
            tokenId, joiningPartyId, myTransportKey, _principalSigner, at, Guid.NewGuid(),
            joiningDmPublicKey: myDmKey,
            joiningXWingPublicKey: Convert.ToBase64String(myXWingKey)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'));

        // 3) Send it over the wire; A admits + returns the bootstrap response (or null on a transport/route error).
        var response = await transport.SendAsync(request, ct).ConfigureAwait(false);
        if (response is null)
        {
            return EnrollmentOutcome.Failed("no_response");
        }

        // 4) Validate A's response against the OUT-OF-BAND invite anchor → the adoption plan (fail-closed).
        var plan = WireEnrollment.ValidateAndPlanAdoption(
            response, inviteAnchor, _principalSigner.IssuerId, joiningPartyId, _verifier, _rosterAuthority, at);
        if (!plan.Succeeded || plan.Roster is null)
        {
            return EnrollmentOutcome.Failed(plan.FailureReason ?? "validation_failed");
        }

        // Capture B's OWN (pre-adoption) genesis team id FIRST — it is the records the supersession purges, and it
        // must be read BEFORE any in-memory swap (which would replace Current.TeamId with A's).
        var ownTeamIdBeforeAdopt = _roster.Current.TeamId;

        // 5) FAIL-CLOSED SUPERSESSION-BEFORE-ADOPT (#1304 F1 — cerebrum [2026-06-21] verdict BLOCKER): supersede B's
        //    OWN superseded-team records from the SYNCED roster doctype BEFORE the irreversible in-memory adopt, so
        //    the JOIN is atomic-ish — it either fully joins or cleanly does not, with NO silent dual-genesis poison.
        //
        //    Why this ordering is correct AND safe to reorder:
        //      · The supersession (CRDT RemoveAt + durable purge) operates ONLY on the synced doctype + durable
        //        store, keyed by the captured own-team id; AdoptEnrollment operates ONLY on the in-memory roster.
        //        They touch DISJOINT state, so the order is free to choose.
        //      · No window is opened by superseding first: before the active-team switch + daemon-rebind (JoinAsync
        //        steps 2-3, which run AFTER EnrollAsync returns) B has NO trust relationship with A, so A's genesis
        //        cannot have converged in yet — removing B's own genesis early cannot drop a record A relies on.
        //
        //    The doctype carries B's OWN genesis (the boot bootstrap seeded it as a synced roster-CRDT record). Once
        //    A's genesis later converges in, the doctype would hold TWO genesis from differing parties → the
        //    FromSyncedRecords injection guard fail-closes the whole roster to Empty() → no convergence. Retracting
        //    B's OWN superseded-team records (a converging CRDT removal + durable purge) leaves only A's genesis to
        //    survive convergence. This is legitimate SELF-supersession (B retracting its OWN records); the
        //    foreign-dual-genesis guard is untouched, so an attacker's 2nd genesis for A's team is STILL rejected.
        //
        //    FAIL-CLOSED on a supersession FAULT: the durable purge does an EF SaveChangesAsync that can throw
        //    (DbUpdateException / SqliteException). If it throws we ABORT the join cleanly — B is left UNCHANGED
        //    (still on its own genesis team in memory; no team-switch, no rebind; the doctype consistent — only B's
        //    own genesis, no dual-genesis poison) and the join FAILS RETRYABLY with a distinct, loud reason. A
        //    re-join re-captures + re-runs the supersession from this same clean state, so the failure cannot leave
        //    B in a persistent silent converge-never state (the catastrophic BLOCKER-2 condition).
        if (_rosterSupersession is not null
            && ownTeamIdBeforeAdopt != Guid.Empty
            && ownTeamIdBeforeAdopt != plan.TeamId)
        {
            try
            {
                await _rosterSupersession
                    .SupersedeOwnTeamRecordsAsync(ownTeamIdBeforeAdopt, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail the JOIN retryably WITHOUT adopting — no poison, no half-state. The caller (JoinAsync → the
                // /admission/join route) surfaces this distinct reason; a re-join retries from the clean own-team
                // state. (OperationCanceledException is re-thrown — a cooperative cancel is not a supersession fault
                // and adopting nothing on cancel is already the fail-closed posture.)
                return EnrollmentOutcome.Failed("roster_supersession_failed");
            }
        }

        // 6) ADOPT A's team — replace B's roster with A's validated roster + set the enrolled-member transport set
        //    (incl. A's key) so MemberSetTrustPolicy trusts every team member. Single-team: B's genesis team is
        //    superseded. B's own A-team subkey is the registrar floor once B's active team is A.teamId. Reached ONLY
        //    after the supersession succeeded (or was not needed), so the synced doctype is already clean — the
        //    in-memory swap and the durable state can no longer disagree.
        // C5 — ALSO adopt the enrolled-member DM-key set (validated to live members) so B can derive DM seal keys
        // with every enrolled peer immediately (before the first synced-roster round). B's OWN DM key is the
        // own-key floor it seeds at bootstrap (Program.cs SetOwnDmPublicKey); the plan carries the PEERS' keys.
        _roster.AdoptEnrollment(plan.Roster, plan.TransportKeysByParty, plan.DmKeysByParty);
        // Seed B's own DM key for the ADOPTED team into the roster's DM map (its own-key floor for A's team).
        _roster.SetOwnDmPublicKey(_selfPartyId, myDmKey);

        return EnrollmentOutcome.Adopted(plan.TeamId, myTransportKey);
    }
}

/// <summary>
/// The wire transport for the joiner enroll exchange — POSTs the <see cref="EnrollmentRequest"/> to the
/// admitter's <c>/admission/redeem</c> route and returns the <see cref="EnrollmentResponse"/> (or null on a
/// transport / non-success route error). The host wires an HTTP-backed implementation; tests wire an in-process
/// one that calls A's route handler directly.
/// </summary>
public interface IEnrollmentTransport
{
    /// <summary>Send the enroll request to the admitter; return A's response, or null when A rejected / the
    /// transport failed.</summary>
    Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct);
}

/// <summary>The outcome of a joiner enrollment — the adopted team id + B's A-team transport key on success, or
/// the fail-closed reason. On failure B adopted NOTHING.</summary>
public sealed record EnrollmentOutcome
{
    private EnrollmentOutcome(bool succeeded, string? failureReason, Guid teamId, byte[]? transportPublicKey)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
        TeamId = teamId;
        TransportPublicKey = transportPublicKey;
    }

    /// <summary>True iff B validated A's response and adopted A's team.</summary>
    public bool Succeeded { get; }

    /// <summary>The fail-closed reason when not succeeded; otherwise null.</summary>
    public string? FailureReason { get; }

    /// <summary>The team id B adopted (= A.teamId) on success.</summary>
    public Guid TeamId { get; }

    /// <summary>B's A-team-scoped transport public key (the HELLO key it now presents); null on failure.</summary>
    public byte[]? TransportPublicKey { get; }

    internal static EnrollmentOutcome Adopted(Guid teamId, byte[] transportPublicKey) =>
        new(true, null, teamId, transportPublicKey);

    internal static EnrollmentOutcome Failed(string reason) =>
        new(false, reason, Guid.Empty, null);
}
