using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Diagnostics;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The A-SIDE admit-and-respond CORE of two-sided wire enrollment — the ONE place that turns a verified
/// <see cref="EnrollmentRequest"/> into mutual-trust state on the admitter node and an
/// <see cref="EnrollmentResponse"/> bootstrap payload. It is the de-duplicated core shared by BOTH admitter
/// transports:
/// <list type="bullet">
///   <item>the loopback <c>POST /admission/redeem</c> route (<see cref="Health.AdmissionRoutes"/>) — same-node /
///     local-app use;</item>
///   <item>the NETWORK pre-trust enrollment channel on the 7473 sync listener
///     (<see cref="NodeEnrollmentAdmitter"/>, #1301 F-1) — the CROSS-MACHINE path a remote B reaches.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (council finding).</b> The redeem logic was duplicated across the HTTP route and the test's
/// in-process transport. #1301 F-1 adds a THIRD caller (the network channel); rather than triplicate it, the full
/// side-effecting sequence lives here once. The PROTOCOL itself (<see cref="WireEnrollment"/>) is reviewed-sound
/// and unchanged — this only reuses it from one shared seam.
/// </para>
/// <para>
/// <b>The sequence (every step fail-closed, identical to the proven route).</b>
/// <list type="number">
///   <item><see cref="WireEnrollment.VerifyRequest"/> — the joiner's proof-of-possession over its
///     (token, party, transport-key, principal-key) binding. A forged / unsigned / key-mismatched request never
///     admits anyone.</item>
///   <item><see cref="AdmissionCoordinator.AdmitOverInvite"/> — redeems the invite single-use (replay / expiry /
///     unknown → fail-closed) and SIGNS the joining (party, principal-key) into the roster
///     (<see cref="MemberRoster.Admit"/> — no-escalation + admitter-key-binding enforced inside).</item>
///   <item><see cref="NodeTeamRoster.AdmitPeer"/> — wire the admitted peer's TRANSPORT key into A's trust map
///     additively (so A's <c>MemberSetTrustPolicy</c> trusts B's wire HELLO).</item>
///   <item>publish the new admission to the roster-sync doctype so it converges to every peer.</item>
///   <item>record the SoD compensating-control audit event (the "second set of eyes") — fail-safe-but-loud.</item>
///   <item><see cref="WireEnrollment.BuildResponse"/> — A's team-scoped transport key + the team genesis anchor +
///     the synced roster + the per-member transport-key map: the bootstrap B adopts.</item>
/// </list>
/// </para>
/// <para>
/// <b>AUTH is the INVITE.</b> This core is gated entirely by the single-use, roster-admin-signed, TTL invite the
/// request carries (step 2). It does NOT consult the F1 loopback session token (that gates the loopback
/// <em>transport</em>, not this core) and does NOT require the joiner to be in the roster (B is JOINING). That is
/// what lets the SAME core serve the cross-machine pre-trust network channel where B has neither.
/// </para>
/// </remarks>
public sealed class WireEnrollmentAdmitter
{
    private readonly AdmissionCoordinator _coordinator;
    private readonly NodeTeamRoster _roster;
    private readonly IOperationSigner _admitterSigner;
    private readonly string _admitterPartyId;
    private readonly IOperationVerifier _verifier;
    private readonly RosterCrdtProjection _projection;
    private readonly IEnrollmentCompensatingControlRecorder _sodAudit;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly CommsDiagnostics _diag;

    public WireEnrollmentAdmitter(
        AdmissionCoordinator coordinator,
        NodeTeamRoster roster,
        IOperationSigner admitterSigner,
        string admitterPartyId,
        IOperationVerifier verifier,
        RosterCrdtProjection projection,
        IEnrollmentCompensatingControlRecorder sodAudit,
        IActiveTeamAccessor activeTeam,
        CommsDiagnostics? diag = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _admitterSigner = admitterSigner ?? throw new ArgumentNullException(nameof(admitterSigner));
        ArgumentException.ThrowIfNullOrWhiteSpace(admitterPartyId);
        _admitterPartyId = admitterPartyId;
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _sodAudit = sodAudit ?? throw new ArgumentNullException(nameof(sodAudit));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        // ADDITIVE DEV DIAGNOSTICS (default-on pre-release): observe the admit decision below; never affects control
        // flow. Null ⇒ the disabled no-op instance (a minimal DI test / the production-off posture).
        _diag = diag ?? CommsDiagnostics.Disabled;
    }

    /// <summary>
    /// Admit the joiner the verified <paramref name="request"/> names and build the bootstrap response. Returns a
    /// fail-closed <see cref="AdmitOutcome"/>: the response on success, or a coarse reject reason. NEVER throws for
    /// an expected reject (a forged request / replayed token / not-ready team) — the network handler relies on
    /// that so it can write a bare fail-closed reply with no roster/genesis leak.
    /// </summary>
    public async Task<AdmitOutcome> AdmitAsync(EnrollmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ADDITIVE DEV DIAGNOSTICS — record the redeem step + the team-scope of B's presented transport key (length
        // only). Observes the inputs; no control-flow effect.
        _diag.TokenRedeemStep("verify-request");

        // (1) Verify the joiner's signature over its binding (proof-of-possession). Fail-closed.
        if (!WireEnrollment.VerifyRequest(request, _verifier))
        {
            _diag.AdmitOutcome(accepted: false, "enroll_signature_invalid", request.JoiningPartyId);
            return AdmitOutcome.Reject("enroll_signature_invalid");
        }

        // Parse the joiner principal + transport key. Malformed → fail-closed.
        PrincipalId joiningPrincipal;
        byte[] joiningTransportKey;
        byte[]? joiningDmKey = null;
        try
        {
            joiningPrincipal = PrincipalId.FromBase64Url(request.JoiningPrincipalPublicKey);
            joiningTransportKey = PrincipalId.FromBase64Url(request.JoiningTransportPublicKey).AsSpan().ToArray();
            // C5 — parse B's DM public key when present (optional + additive; a legacy joiner sends none). The
            // signature verified above already bound this exact DM key, so A records exactly what B proved it holds.
            if (!string.IsNullOrEmpty(request.JoiningDmPublicKey))
            {
                joiningDmKey = PrincipalId.FromBase64Url(request.JoiningDmPublicKey).AsSpan().ToArray();
            }
        }
        catch (FormatException)
        {
            _diag.AdmitOutcome(accepted: false, "invalid_key", request.JoiningPartyId);
            return AdmitOutcome.Reject("invalid_key");
        }
        if (joiningTransportKey.Length != PrincipalId.LengthInBytes)
        {
            _diag.AdmitOutcome(accepted: false, "invalid_transport_key", request.JoiningPartyId);
            return AdmitOutcome.Reject("invalid_transport_key");
        }
        if (joiningDmKey is not null && joiningDmKey.Length != PrincipalId.LengthInBytes)
        {
            _diag.AdmitOutcome(accepted: false, "invalid_dm_key", request.JoiningPartyId);
            return AdmitOutcome.Reject("invalid_dm_key");
        }

        // ADDITIVE DEV DIAGNOSTICS — the joiner's presented transport key parsed OK; record its length + scope (the
        // teamId is the active team A admits into). Length only — never the key value.
        _diag.KeyScoping("transport", _activeTeam.Active?.TeamId.Value.ToString("D"), joiningTransportKey.Length);

        // (2) Redeem single-use + admit (no-escalation + admitter-key-binding enforced inside Admit). A guard
        //     violation (admitter not a member, joiner already a member) is a fail-closed reject, not a throw.
        InviteAdmissionResult result;
        try
        {
            // C5 (DM key-substitution fix) — bind B's DM public key INTO the signed admission. B's DM key was bound
            // into B's signed enrollment request and VerifyRequest re-checked it above, so A signs exactly the DM key
            // B proved possession of. Pass the base64url DM key (empty when the joiner presented none).
            var joiningDmB64 = request.JoiningDmPublicKey ?? string.Empty;
            // #3167 R3/C5-X — ALSO bind B's X-Wing public key INTO the signed admission (a confidentiality key like
            // the DM key). VerifyRequest re-checked the joiner's principal signature over the transcript covering the
            // X-Wing bytes above, so A signs exactly the X-Wing key B proved. Additive/optional on the PLAIN path
            // (empty when the joiner presented none → byte-stable); the pairing route makes it mandatory. A malformed
            // value is a safe degrade (peers treat a wrong-length carried X-Wing key as absent → suite #1).
            var joiningXWingB64 = request.JoiningXWingPublicKey ?? string.Empty;
            result = _coordinator.AdmitOverInvite(
                _roster.Current,
                request.TokenId,
                _admitterPartyId,
                _admitterSigner,
                request.JoiningPartyId,
                joiningPrincipal,
                PermissionCompositions.Member,
                joiningDmB64,
                joiningXWingB64);
        }
        catch (RosterGuardException)
        {
            _diag.AdmitOutcome(accepted: false, "admit_rejected", request.JoiningPartyId);
            return AdmitOutcome.Reject("admit_rejected");
        }

        if (!result.Admitted_ || result.Roster is null)
        {
            // Single-use / TTL / unknown-token rejection — opaque (never disclose which).
            _diag.AdmitOutcome(accepted: false, "invite_rejected", request.JoiningPartyId);
            return AdmitOutcome.Reject("invite_rejected");
        }
        _diag.TokenRedeemStep("redeemed-single-use");

        var newRoster = result.Roster;

        // (3) Wire the admitted peer's TRANSPORT key into A's trust map additively — A now trusts B's wire HELLO.
        //     C5 — ALSO record B's DM public key (when present) so A can derive a per-conversation DM seal key with B.
        _roster.AdmitPeer(newRoster, request.JoiningPartyId, joiningTransportKey, joiningDmKey);

        // (4) Publish the new admission to the roster-sync doctype so it converges to every peer — STAMPING the
        //     joiner's team-scoped TRANSPORT key onto the synced record (INFO-2; the ≥3-node mesh fix). A holds the
        //     joiner's transport key (the joiner supplied it over the admission channel; it is what AdmitPeer just
        //     recorded), so the synced admission record now CARRIES it. Every converging member (including peers
        //     admitted earlier, e.g. B when this admits C) harvests it from the converged roster and trusts the new
        //     peer's wire HELLO directly — not only via the admitting hub. The key rides UNSIGNED-by-association,
        //     outside the signed admission payload, honored only because this party is in the genesis-rooted roster.
        var newAdmission = newRoster.EnumerateAdmissions()
            .FirstOrDefault(a => string.Equals(a.PartyId, request.JoiningPartyId, StringComparison.Ordinal));
        if (newAdmission is not null)
        {
            // C5 — ALSO stamp B's DM public key onto the synced record so every converging member harvests it
            // (roster-derived DM keys; the #1310 pattern extended to the DM half).
            await _projection.PublishLocalAsync(
                RosterRecordCrdtState.FromAdmission(newAdmission, joiningTransportKey, joiningDmKey), ct)
                .ConfigureAwait(false);
        }

        // (5) SoD compensating-control audit (the second set of eyes) — fail-safe-but-loud (host-wired onFault).
        var grantedPermissions = newRoster.PermissionsOf(request.JoiningPartyId)?.Permissions
            ?? Array.Empty<string>();
        var admittedPublicKey = newRoster.PublicKeyOf(request.JoiningPartyId)?.ToBase64Url()
            ?? request.JoiningPrincipalPublicKey;
        await _sodAudit.RecordMemberAdmittedAsync(
            tenantId: NodeTenant.Resolve(_activeTeam),
            teamId: newRoster.TeamId.ToString("D"),
            admitterPartyId: _admitterPartyId,
            admittedPartyId: request.JoiningPartyId,
            admittedPublicKeyBase64Url: admittedPublicKey,
            grantedPermissions: grantedPermissions.ToArray(),
            admissionMode: "invite",
            correlationId: System.Diagnostics.Activity.Current?.Id,
            ct: ct).ConfigureAwait(false);

        // (6) Build the A→B bootstrap response. A's team-scoped transport key comes from the ACTIVE TEAM's
        //     INodeIdentityProvider (the #1296-F2 source of truth — the SAME key A presents on the wire HELLO).
        //     No active team → no team-scoped transport key → fail-closed (the admission committed, but B cannot
        //     adopt without A's HELLO key; B retries once the team is ready).
        var teamIdentity = _activeTeam.Active?.Services.GetService<INodeIdentityProvider>();
        if (teamIdentity is null)
        {
            _diag.AdmitOutcome(accepted: false, "team_not_ready", request.JoiningPartyId);
            return AdmitOutcome.Reject("team_not_ready");
        }
        var admitterTransportKey = (byte[])teamIdentity.Current.PublicKey.Clone();

        var memberTransportKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [_admitterPartyId] = admitterTransportKey,
        };
        foreach (var (party, key) in _roster.AdmittedPeerTransportKeys())
        {
            memberTransportKeys[party] = key;
        }

        // C5 — the per-member DM-key map for the response: A's own DM public key (so B can DM A) + every admitted
        // peer's DM key A knows (incl. the just-admitted B, echoed back). A's own DM key is the genesis/self entry
        // in the roster's DM map (seeded at bootstrap, harvested from the synced genesis record). When A has no DM
        // key wired (a legacy/minimal host) the map simply omits it — B learns it later from the synced roster.
        var memberDmKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var admitterDmKey = _roster.DmPublicKeyOf(_admitterPartyId);
        if (admitterDmKey is { Length: > 0 }) memberDmKeys[_admitterPartyId] = admitterDmKey;
        foreach (var (party, key) in _roster.AdmittedPeerDmKeys())
        {
            memberDmKeys[party] = key;
        }

        var response = WireEnrollment.BuildResponse(
            newRoster, admitterTransportKey, memberTransportKeys, memberDmKeys);
        // ADDITIVE DEV DIAGNOSTICS — the admit succeeded; record the accepted party + the admitter's own team-scoped
        // transport key length (never the value), the key the joiner will trust on A's wire HELLO.
        _diag.KeyScoping("transport(admitter-self)", newRoster.TeamId.ToString("D"), admitterTransportKey.Length);
        _diag.AdmitOutcome(accepted: true, reason: null, request.JoiningPartyId);
        return AdmitOutcome.Accept(response);
    }

    /// <summary>The outcome of an A-side admit — the bootstrap response on success, or a coarse fail-closed reject
    /// reason that never discloses roster / genesis state.</summary>
    public sealed record AdmitOutcome
    {
        private AdmitOutcome(bool accepted, EnrollmentResponse? response, string? rejectReason)
        {
            Accepted = accepted;
            Response = response;
            RejectReason = rejectReason;
        }

        public bool Accepted { get; }
        public EnrollmentResponse? Response { get; }
        public string? RejectReason { get; }

        public static AdmitOutcome Accept(EnrollmentResponse response) => new(true, response, null);
        public static AdmitOutcome Reject(string reason) => new(false, null, reason);
    }
}
