using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Diagnostics;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// MTW-2 #3167 (R1.3 + R4-a) — the WEB-ADMITTED-MEMBER PAIRING admit-and-respond core. The pairing analogue of
/// <see cref="WireEnrollmentAdmitter"/>: it turns a verified <see cref="EnrollmentRequest"/> from a web-admitted
/// member's first device into mutual-trust state + an <see cref="EnrollmentResponse"/> bootstrap — but via the
/// TOKEN-GATED, pin-re-verifying <see cref="WebAdmittedMemberAtlasBridge"/> and a DISTINCT, structurally-unreachable
/// admitter signer, so the node NEVER admits on this path except by mechanically transcribing a redeemed
/// pairing-token authorization into roster form (admiral-ruling-2026-07-24T1150Z R1 + amendment R4-a).
/// </summary>
/// <remarks>
/// <para>
/// <b>R1.3 — token-DERIVED authority, structurally enforced (NOT convention).</b> The pairing admitter signer is
/// held PRIVATELY here (<see cref="_pairingAdmitterSigner"/>) and is a DISTINCT instance from
/// <see cref="WireEnrollmentAdmitter"/>'s standing <c>_admitterSigner</c> (the 1150Z prohibition on reusing the
/// standing signer for this path). No property exposes it; the ONLY code that reaches it is
/// <see cref="AdmitAsync"/>, which hands it to <see cref="WebAdmittedMemberAtlasBridge.AdmitFromPairingTokenAsync"/>
/// — and that path SIGNS only after <see cref="AdmissionCoordinator.AdmitOverInvite"/> obtains a REDEMPTION RECEIPT
/// (the <c>RedeemResult.Accepted</c> returned by the single-use token-store <c>Redeem</c>). No redeem ⇒ no signature.
/// A token that was never minted/bound, a replayed/expired token, or any pin drift all fail-closed WITHOUT a
/// signature. The distinct-signer + single-caller + redeem-gated-sign chain is what makes "the signer is
/// unreachable without a successful redemption" a STRUCTURAL property, not a documented promise (tested:
/// <c>PairingTokenGatedAdmitterTests</c>).
/// </para>
/// <para>
/// <b>R4-a — proof-of-possession BEFORE redeem (F3 ordering; the token stays unburned on failure).</b> The wire
/// boundary requires the enrolling device to prove control of the presented keys before ANY redemption:
/// <see cref="WireEnrollment.VerifyRequest"/> re-checks the joiner's Ed25519 PRINCIPAL-key signature over the
/// enrollment transcript, which COVERS the token id, the DM public-key bytes, and the X-Wing public-key bytes
/// (the amendment's ratified mechanism — X25519 DM keys cannot self-sign, so the principal signature is the
/// possession/binding proof). It runs FIRST; a forged / unsigned / key-substituted request refuses before the
/// bridge is ever called, so the single-use pairing token is not consumed (no griefing / DoS on the member's own
/// token). The X-Wing key is then MANDATORY on this path (R3): absent or not exactly the 1216-byte X-Wing length
/// ⇒ refuse (still pre-redeem). foundation-identity-atlas stays X-Wing-size-agnostic; the length gate lives HERE.
/// </para>
/// <para>
/// <b>Post-admit sequence (steps 3–6, identical to the proven <see cref="WireEnrollmentAdmitter"/> tail).</b> After
/// the bridge records the signed, genesis-rooted admission (token id + mint-session evidence bound in — R1.2), this
/// wires the admitted peer's transport + DM keys into A's trust map (<see cref="NodeTeamRoster.AdmitPeer"/>),
/// publishes the signed admission to the roster-sync doctype (<see cref="RosterCrdtProjection.PublishLocalAsync"/> —
/// the X-Wing key rides the SIGNED admission, harvested on convergence), records the SoD compensating-control audit
/// event with mode <c>web-pairing</c>, and builds the A→B bootstrap response
/// (<see cref="WireEnrollment.BuildResponse"/>).
/// </para>
/// <para>
/// <b>F4 — every refusal is ONE opaque outcome.</b> <see cref="AdmitAsync"/> returns a coarse
/// <see cref="PairingAdmitOutcome"/> that NEVER discloses which of the many distinct internal causes fired; the
/// route (<see cref="Health.AdmissionRoutes"/>) collapses every non-accept to a single byte-identical opaque wire
/// response (status + headers + body — F4-a). The distinct cause is surfaced only to the ADDITIVE dev diagnostics.
/// </para>
/// <para>
/// <b>Fail-closed team-readiness BEFORE commit (a #3167 tightening over <see cref="WireEnrollmentAdmitter"/>).</b>
/// A's team-scoped transport identity is resolved BEFORE the bridge redeem, so a not-yet-materialized team refuses
/// WITHOUT burning the token (the member retries once bootstrap completes; the token is still redeemable). This
/// keeps EVERY pairing refusal — including team-not-ready — a pre-redeem, token-preserving, opaque outcome.
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
internal sealed class PairingTokenGatedAdmitter
{
    private readonly WebAdmittedMemberAtlasBridge _bridge;
    private readonly NodeTeamRoster _roster;

    // R1.3 — the DISTINCT pairing admitter signer, held PRIVATELY. NOT WireEnrollmentAdmitter._admitterSigner.
    // Never exposed; reachable only through AdmitAsync → the token-gated bridge, whose sign is gated on a redeem.
    private readonly IOperationSigner _pairingAdmitterSigner;
    private readonly string _admitterPartyId;
    private readonly IOperationVerifier _verifier;
    private readonly RosterCrdtProjection _projection;
    private readonly IEnrollmentCompensatingControlRecorder _sodAudit;
    private readonly IWebPairingInviteBindingStore _bindings;
    private readonly ITeamContextFactory _teamContexts;
    private readonly CommsDiagnostics _diag;

    public PairingTokenGatedAdmitter(
        WebAdmittedMemberAtlasBridge bridge,
        NodeTeamRoster roster,
        IOperationSigner pairingAdmitterSigner,
        string admitterPartyId,
        IOperationVerifier verifier,
        RosterCrdtProjection projection,
        IEnrollmentCompensatingControlRecorder sodAudit,
        IWebPairingInviteBindingStore bindings,
        ITeamContextFactory teamContexts,
        CommsDiagnostics? diag = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _pairingAdmitterSigner = pairingAdmitterSigner ?? throw new ArgumentNullException(nameof(pairingAdmitterSigner));
        ArgumentException.ThrowIfNullOrWhiteSpace(admitterPartyId);
        _admitterPartyId = admitterPartyId;
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _sodAudit = sodAudit ?? throw new ArgumentNullException(nameof(sodAudit));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _teamContexts = teamContexts ?? throw new ArgumentNullException(nameof(teamContexts));
        _diag = diag ?? CommsDiagnostics.Disabled;
    }

    /// <summary>
    /// Admit the web-admitted member's first device the verified <paramref name="request"/> names (via its bound
    /// pairing token) and build the bootstrap response. Returns a fail-closed <see cref="PairingAdmitOutcome"/>:
    /// the response on success, or a coarse reject reason (never thrown for an expected reject — the route relies on
    /// that to emit one opaque wire reply with no roster/genesis leak). Exceptions from the redeem/sign step
    /// (durable-store throw / <see cref="CryptographicException"/>) are CONTAINED to an opaque refusal — never an
    /// escaping 500 (R6/C).
    /// </summary>
    public async Task<PairingAdmitOutcome> AdmitAsync(EnrollmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var binding = _bindings.Lookup(request.TokenId);
        return binding is null
            ? PairingAdmitOutcome.Refuse("pairing_binding_unknown")
            : await AdmitAsync(request, binding, ct).ConfigureAwait(false);
    }

    internal async Task<PairingAdmitOutcome> AdmitAsync(
        EnrollmentRequest request,
        WebPairingInviteBinding binding,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);

        if (!string.Equals(binding.TokenId, request.TokenId, StringComparison.Ordinal))
        {
            return PairingAdmitOutcome.Refuse("pairing_binding_mismatch");
        }

        _diag.TokenRedeemStep("pairing-verify-request");

        // (1) R4-a — proof-of-possession over the enrollment transcript (Ed25519 principal signature covering the
        //     token id + DM + X-Wing bytes) BEFORE any redemption. Fail-closed; the token is NOT consumed here.
        if (!WireEnrollment.VerifyRequest(request, _verifier))
        {
            _diag.AdmitOutcome(accepted: false, "enroll_signature_invalid", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("enroll_signature_invalid");
        }

        // (2) Parse the joiner principal + transport + DM keys. Malformed → fail-closed (pre-redeem).
        PrincipalId joiningPrincipal;
        byte[] joiningTransportKey;
        byte[]? joiningDmKey = null;
        try
        {
            joiningPrincipal = PrincipalId.FromBase64Url(request.JoiningPrincipalPublicKey);
            joiningTransportKey = PrincipalId.FromBase64Url(request.JoiningTransportPublicKey).AsSpan().ToArray();
            if (!string.IsNullOrEmpty(request.JoiningDmPublicKey))
            {
                joiningDmKey = PrincipalId.FromBase64Url(request.JoiningDmPublicKey).AsSpan().ToArray();
            }
        }
        catch (FormatException)
        {
            _diag.AdmitOutcome(accepted: false, "invalid_key", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("invalid_key");
        }
        if (joiningTransportKey.Length != PrincipalId.LengthInBytes)
        {
            _diag.AdmitOutcome(accepted: false, "invalid_transport_key", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("invalid_transport_key");
        }
        if (joiningDmKey is not null && joiningDmKey.Length != PrincipalId.LengthInBytes)
        {
            _diag.AdmitOutcome(accepted: false, "invalid_dm_key", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("invalid_dm_key");
        }

        // (3) R3 — X-Wing is MANDATORY on the pairing path (F2 key-threading is only meaningful if BOTH keys
        //     arrive). Absent or not exactly the 1216-byte X-Wing length ⇒ refuse (still pre-redeem; token unburned).
        //     The transcript-covered X-Wing bytes were already possession-bound by VerifyRequest above.
        var xwingB64 = request.JoiningXWingPublicKey ?? string.Empty;
        if (xwingB64.Length == 0)
        {
            _diag.AdmitOutcome(accepted: false, "xwing_required", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("xwing_required");
        }
        if (!IsWellFormedXWingKey(xwingB64))
        {
            _diag.AdmitOutcome(accepted: false, "invalid_xwing_key", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("invalid_xwing_key");
        }

        // (4) Resolve A's team-scoped transport identity BEFORE the redeem/commit. A not-yet-materialized team
        //     refuses WITHOUT burning the token (the member retries once bootstrap completes; the token stays
        //     redeemable). This is the #3167 tightening over WireEnrollmentAdmitter (which resolves this AFTER the
        //     commit and would burn the token on team-not-ready).
        var roster = _roster.Resolve(binding.Anchor);
        if (roster is null || !Guid.TryParse(binding.Anchor.TeamId, out var anchorTeamId))
        {
            _diag.AdmitOutcome(accepted: false, "token_team_unavailable", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("token_team_unavailable");
        }
        var teamContext = _teamContexts.Active.SingleOrDefault(
            candidate => candidate.TeamId == new TeamId(anchorTeamId));
        var teamIdentity = teamContext?.Services.GetService<INodeIdentityProvider>();
        if (teamIdentity is null)
        {
            _diag.AdmitOutcome(accepted: false, "team_not_ready", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("team_not_ready");
        }
        var admitterTransportKey = (byte[])teamIdentity.Current.PublicKey.Clone();

        // (5) R1.3 — admit through the TOKEN-GATED bridge with the DISTINCT PRIVATE pairing signer. The bridge
        //     re-verifies the four web-plane pins against the enrollment, then AdmitOverInvite REDEEMS the pairing
        //     token single-use (durable CAS) and — ONLY on redeem.Accepted (the redemption receipt) — SIGNS the
        //     admission (token id + mint-session evidence bound in). The signer is reachable ONLY here; no redeem,
        //     no signature. R6/C — a durable-store Redeem throw or a CryptographicException is CONTAINED to an
        //     opaque refusal (never an escaping 500); OperationCanceledException propagates (cancellation is real).
        WebAdmittedMemberAtlasBridge.AtlasAdmissionOutcome bridgeOutcome;
        try
        {
            bridgeOutcome = await _bridge.AdmitFromPairingDecisionAsync(
                roster,
                _admitterPartyId,
                _pairingAdmitterSigner,
                binding,
                request.JoiningPartyId,
                joiningPrincipal,
                request.JoiningDmPublicKey ?? string.Empty,
                xwingB64,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // R6/C exception containment beyond the bridge's own RosterGuardException handling. A durable token-store
            // Redeem throw (DB fault) or a CryptographicException from signing MUST NOT escape as a 500 (an
            // availability + information signal). Fail-closed to the single opaque refusal; the distinct cause is
            // recorded only to the additive dev diagnostics.
            _diag.AdmitOutcome(accepted: false, $"pairing_admit_faulted:{ex.GetType().Name}", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("enrollment_refused");
        }

        // The bridge collapses EVERY internal refusal to one opaque wire outcome (F4). Map to our fail-closed reject.
        var wire = bridgeOutcome.ToWire();
        if (!wire.Admitted || wire.Roster is null)
        {
            _diag.AdmitOutcome(accepted: false, "enrollment_refused", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("enrollment_refused");
        }
        _diag.TokenRedeemStep("pairing-redeemed-single-use");

        var newRoster = wire.Roster;

        // (6) Post-admit step 3 — wire the admitted peer's TRANSPORT + DM keys into A's trust map. The X-Wing key is
        //     NOT wired here: it rides the SIGNED admission and is harvested from the chain-validated roster on
        //     convergence (the C5-X model), exactly as in WireEnrollmentAdmitter.
        if (!_roster.TryAdmitPeer(
                binding.Anchor, newRoster, request.JoiningPartyId, joiningTransportKey, joiningDmKey))
        {
            _diag.AdmitOutcome(accepted: false, "token_team_changed", request.JoiningPartyId);
            return PairingAdmitOutcome.Refuse("token_team_changed");
        }

        // (7) Post-admit step 4 — publish the signed admission to the roster-sync doctype so it converges to peers.
        //     FromAdmission derives the wire DM + X-Wing fields from the SIGNED admission, so both ride to peers.
        var newAdmission = newRoster.EnumerateAdmissions()
            .FirstOrDefault(a => string.Equals(a.PartyId, request.JoiningPartyId, StringComparison.Ordinal));
        if (newAdmission is not null)
        {
            await _projection.PublishLocalAsync(
                RosterRecordCrdtState.FromAdmission(newAdmission, joiningTransportKey, joiningDmKey), ct)
                .ConfigureAwait(false);
        }

        // (8) Post-admit step 5 — SoD compensating-control audit (fail-safe-but-loud). Mode "web-pairing" so the
        //     audit trail distinguishes a web-admitted-member device pairing from a plain invite.
        // Ticket 293 slice 5 — the set the bridge CONFERRED on this admission (signed into the admission and
        // staged as the party's own grant), carried back on the outcome. The roster cannot answer this: since
        // slice 3b2 a replicated member carries no permission set, so reading it back would audit an empty set.
        var grantedPermissions = bridgeOutcome.ConferredPermissions?.Permissions ?? Array.Empty<string>();
        var admittedPublicKey = newRoster.PublicKeyOf(request.JoiningPartyId)?.ToBase64Url()
            ?? request.JoiningPrincipalPublicKey;
        await _sodAudit.RecordMemberAdmittedAsync(
            tenantId: new TenantId(binding.Membership.TenantId),
            teamId: newRoster.TeamId.ToString("D"),
            admitterPartyId: _admitterPartyId,
            admittedPartyId: request.JoiningPartyId,
            admittedPublicKeyBase64Url: admittedPublicKey,
            grantedPermissions: grantedPermissions.ToArray(),
            admissionMode: "web-pairing",
            correlationId: System.Diagnostics.Activity.Current?.Id,
            ct: ct).ConfigureAwait(false);

        // (9) Post-admit step 6 — build the A→B bootstrap response (A's team-scoped transport key resolved in step 4;
        //     the admission already committed). The per-member transport + DM maps mirror WireEnrollmentAdmitter.
        var memberTransportKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [_admitterPartyId] = admitterTransportKey,
        };
        foreach (var (party, key) in _roster.AdmittedPeerTransportKeys())
        {
            memberTransportKeys[party] = key;
        }

        var memberDmKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var admitterDmKey = _roster.DmPublicKeyOf(_admitterPartyId);
        if (admitterDmKey is { Length: > 0 }) memberDmKeys[_admitterPartyId] = admitterDmKey;
        foreach (var (party, key) in _roster.AdmittedPeerDmKeys())
        {
            memberDmKeys[party] = key;
        }

        var response = WireEnrollment.BuildResponse(
            newRoster, admitterTransportKey, memberTransportKeys, memberDmKeys);
        _diag.AdmitOutcome(accepted: true, reason: null, request.JoiningPartyId);
        return PairingAdmitOutcome.Accept(response);
    }

    /// <summary>
    /// True iff <paramref name="xwingB64"/> is a well-formed base64url encoding of EXACTLY the 1216-byte X-Wing
    /// public-key length. Mirrors the roster's <c>DecodeRawXWingKeyOrThrow</c> discipline: normalize base64url →
    /// pad → decode → length-check. A non-base64url or wrong-length value is not well-formed (fail-closed).
    /// </summary>
    private static bool IsWellFormedXWingKey(string xwingB64)
    {
        try
        {
            var normalized = xwingB64.Replace('-', '+').Replace('_', '/');
            var padded = (normalized.Length % 4) switch
            {
                2 => normalized + "==",
                3 => normalized + "=",
                0 => normalized,
                _ => null,
            };
            if (padded is null) return false;
            var bytes = Convert.FromBase64String(padded);
            return bytes.Length == RosterRecordCrdtState.XWingPublicKeyLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The outcome of a pairing admit — the bootstrap response on success, or a coarse fail-closed reject
    /// reason that never discloses roster / grant / token state. The route collapses ANY reject to one opaque wire
    /// reply (F4-a).</summary>
    internal sealed record PairingAdmitOutcome
    {
        private PairingAdmitOutcome(bool accepted, EnrollmentResponse? response, string? rejectReason)
        {
            Accepted = accepted;
            Response = response;
            RejectReason = rejectReason;
        }

        public bool Accepted { get; }
        public EnrollmentResponse? Response { get; }

        /// <summary>The coarse, PII-free reject reason (audit/diagnostics ONLY — never crosses the wire).</summary>
        public string? RejectReason { get; }

        public static PairingAdmitOutcome Accept(EnrollmentResponse response) => new(true, response, null);
        public static PairingAdmitOutcome Refuse(string reason) => new(false, null, reason);
    }
}
