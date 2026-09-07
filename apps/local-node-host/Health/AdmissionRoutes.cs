using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Diagnostics;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The RUNTIME ADMISSION surface (gap #3) — the thin node-local route over <see cref="AdmissionCoordinator"/>
/// that lets a RUNNING node drive peer admission (the two-user harness drove the coordinator directly; there was
/// no runtime caller). It is the backend the Harborline App QR-scan / invite-entry UI (a flagged FED follow-on) calls;
/// this is the route + a thin client seam, NOT the UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two routes (mirror the two confirmed admission modes).</b>
/// <list type="bullet">
///   <item><c>POST /api/local-node/admission/invites</c> — GENERATE-INVITE. The local admin (who holds
///     <c>members:admit</c>) mints a single-use, short-TTL invite off <see cref="TeamTrustAnchor.FromRoster"/>;
///     the Harborline App delivers it out-of-band (QR / link). Returns the token id + anchor + expiry.</item>
///   <item><c>POST /api/local-node/admission/redeem</c> — REDEEM-INVITE. A joining node presents the invite
///     token id + its (party id, principal pubkey, team-scoped transport pubkey). The local admin redeems the
///     token single-use (replay/expiry rejected) and SIGNS the joining (party, principal-key) into the roster —
///     a signed admission that is then PUBLISHED to the roster-sync doctype (so it converges to every peer) AND
///     wired into the transport-trust map (so <c>MemberSetTrustPolicy</c> trusts the new peer for sync).</item>
/// </list>
/// </para>
/// <para>
/// <b>The transport-key linkage (gap #3 core).</b> A member's team-scoped TRANSPORT subkey is
/// HKDF(that member's root PRIVATE key, teamId) — material the ADMITTING node never holds. So the JOINING node
/// supplies its own transport PUBLIC key in the redeem request (alongside its principal pubkey), exactly as the
/// proximity/QR channel already carries the joiner's pubkey. On admit, that transport key enters
/// <see cref="NodeTeamRoster.AdmitPeer"/> so the peer is trusted for sync; on a SYNCED revocation,
/// <see cref="NodeTeamRoster.AdoptSyncedRoster"/> drops it (the revoked member loses transport-trust
/// post-convergence). This is the wiring that makes an admitted member able to sync and a revoked member unable
/// to — beyond mere attribution.
/// </para>
/// <para>
/// <b>What is signed vs. what is local (do NOT conflate).</b> The admitter's signature covers the
/// (party, principal-key) binding ONLY — that is the <see cref="MemberRoster.Admit"/> / roster-sync record that
/// is cryptographically attested and propagated to every peer. The team-scoped TRANSPORT key is <b>UNSIGNED and
/// local to the admitting node</b>: it is a trust-set hint for <c>MemberSetTrustPolicy</c> on THIS node's
/// sync-session gate, NOT part of the signed admission and NOT carried on the synced roster record. (Consequence:
/// transport-trust is established only on the admitting node; a ≥3-node mesh where a third node must learn an
/// admitted peer's transport key is a flagged follow-on — the transport key would need to ride a signed record,
/// or each node derives it from its own admission channel.)
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) gates these routes by DEFAULT.</b> They are NOT in the
/// <c>SharedHostedWebApp.CallerAuthAllowlist</c> (only /health + /ws are), so the listener-level middleware
/// rejects a tokenless caller fail-closed (401) when a session token is configured — admission is an
/// admin-only operation and must never be reachable by a stranger local process. There is NO per-route opt-out.
/// </para>
/// <para>
/// <b>No-escalation + single-use/TTL are inherited, not re-implemented here.</b> The admit reduces to the local
/// admin signing via <see cref="AdmissionCoordinator.AdmitOverInvite"/> → <see cref="MemberRoster.Admit"/>,
/// which enforces no-escalation (the admitted set ⊆ the admitter's held set) and binds the admitter signer to
/// the admitter's roster key; the invite single-use + TTL are enforced by the <see cref="IAdmissionTokenStore"/>.
/// The local admitter is the seeded genesis member (single-office node), whose signing key the route holds.
/// </para>
/// <para>
/// <b>The SoD compensating control fires HERE (#1295 F1).</b> A successful redeem-invite IS a real runtime
/// member admit — the SoD-significant op the "second set of eyes" audit control exists to record. AFTER the
/// roster mutation succeeds (token redeemed, admission signed + applied + published), this route invokes
/// <see cref="IEnrollmentCompensatingControlRecorder.RecordMemberAdmittedAsync"/> so the admit lands in the immutable, signed,
/// tamper-evident audit trail at RUNTIME — not only in a direct-call unit test (the F1 wiring gap). The sink is
/// fail-safe-but-loud: a recording fault never bricks the admit, but it is signalled (the host wires the loud
/// onFault). The other SoD ops (revoke / permission-grant / ownership-transfer) have no runtime route in this
/// host yet (only admit is exposed), so their <see cref="IEnrollmentCompensatingControlRecorder"/> methods wire when those routes land —
/// the sink is already DI-registered and ready (a named follow-on, not a silent gap).
/// </para>
/// </remarks>
public static class AdmissionRoutes
{
    /// <summary>Canonical route base for the runtime admission surface.</summary>
    public const string RouteBase = "/api/local-node/admission";

    /// <summary>
    /// Maps the admission routes onto <paramref name="app"/>. The coordinator, the install-level roster, the
    /// admitter's signer + party id, the verifier, the roster-sync projection, the SoD audit sink, the active-
    /// team accessor, and the B-side join orchestrator are supplied by the composition root.
    /// </summary>
    /// <remarks>
    /// <b>The node-identity route resolves the TEAM-SCOPED transport key, not an outer-container identity.</b> The
    /// per-team <see cref="INodeIdentityProvider"/> (= HKDF(node-root, activeTeamId)) lives ONLY in the active
    /// team's CHILD container (<c>DefaultTeamServiceRegistrar</c>); the outer host container never registers one
    /// (the host does not call <c>AddHarborlineKernelSync</c>). So the GET /identity handler resolves it from
    /// <see cref="IActiveTeamAccessor.Active"/>'s <c>Services</c> at request time — the SAME source
    /// <c>LocalNodeWorker.WireCrossMachineSyncAsync</c> reads for the wire HELLO key, so the transport key this
    /// route returns is byte-identical to the key this node presents on the sync handshake (#1296 F2). Injecting an
    /// outer-container <c>INodeIdentityProvider</c> would (a) be unresolvable → a startup throw, and (b) even if
    /// present hold the ROOT/own-team key, which the joined team's <c>MemberSetTrustPolicy</c> would never trust.
    /// </remarks>
    /// <remarks>
    /// Internal, like the pairing overload below and for the same reason: it now carries the host-internal
    /// selected-session identity seam. Every caller — the hosted endpoint and the route tests — is inside the
    /// host assembly or sees its internals.
    /// </remarks>
    internal static void Map(
        IEndpointRouteBuilder app,
        AdmissionCoordinator coordinator,
        NodeTeamRoster roster,
        IOperationSigner admitterSigner,
        string admitterPartyId,
        IOperationVerifier verifier,
        RosterCrdtProjection projection,
        IEnrollmentCompensatingControlRecorder sodAudit,
        IActiveTeamAccessor activeTeam,
        Harborline.Api.LocalNodeHost.Enrollment.NodeEnrollmentJoinService? joinService = null,
        CommsDiagnostics? diag = null,
        IWebSelectedSessionIdentityAuthority? selectedIdentity = null)
        => MapCore(
            app, coordinator, roster, admitterSigner, admitterPartyId, verifier, projection, sodAudit,
            activeTeam, joinService, diag, pairing: null, selectedIdentity);

    /// <summary>
    /// #3167 — the FULL admission surface incl. the web-admitted PAIRING path (mode-exclusive with the plain path
    /// per R2). Same as <see cref="Map"/> but the redeem route dispatches on the pairing binding: a token with a
    /// pairing binding routes to the token-gated pairing admitter; a binding-absent redeem refuses opaque when
    /// web-plane admission is enabled for the tenant (R2 — plain path mode-exclusively OFF); else the plain
    /// <see cref="Harborline.Api.LocalNodeHost.Enrollment.WireEnrollmentAdmitter"/> serves. Internal because the pairing
    /// bundle is host-internal; the production caller (<see cref="HostedAdmissionApiEndpoint"/>) is in this assembly.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        AdmissionCoordinator coordinator,
        NodeTeamRoster roster,
        IOperationSigner admitterSigner,
        string admitterPartyId,
        IOperationVerifier verifier,
        RosterCrdtProjection projection,
        IEnrollmentCompensatingControlRecorder sodAudit,
        Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemDispatch pairing,
        Harborline.Api.LocalNodeHost.Enrollment.NodeEnrollmentJoinService? joinService = null,
        CommsDiagnostics? diag = null,
        IWebSelectedSessionIdentityAuthority? selectedIdentity = null)
    {
        System.ArgumentNullException.ThrowIfNull(pairing);
        // The active-team accessor is taken FROM the pairing bundle (the same process-global active-team DI
        // singleton the plain path receives), so the pairing overload adds no separate accessor param and does
        // not double-carry the ADR 0160 R3 active-team seam. The selected-session authority added for the
        // founder web-admission card is a trailing OPTIONAL on both existing overloads for the same reason: a
        // parallel pair of dedicated overloads would have re-introduced a second active-team accessor param,
        // which is exactly the would-be token the 3167 ratchet recorded and the 3228 reduction removed rather
        // than carried. Named in prose deliberately — the legacy-authority debt scanner is a TEXT scan, so
        // spelling the symbol here would register the very token this comment explains we avoided.
        MapCore(
            app, coordinator, roster, admitterSigner, admitterPartyId, verifier, projection, sodAudit,
            pairing.ActiveTeam, joinService, diag, pairing, selectedIdentity);
    }

    private static void MapCore(
        IEndpointRouteBuilder app,
        AdmissionCoordinator coordinator,
        NodeTeamRoster roster,
        IOperationSigner admitterSigner,
        string admitterPartyId,
        IOperationVerifier verifier,
        RosterCrdtProjection projection,
        IEnrollmentCompensatingControlRecorder sodAudit,
        IActiveTeamAccessor activeTeam,
        Harborline.Api.LocalNodeHost.Enrollment.NodeEnrollmentJoinService? joinService,
        CommsDiagnostics? diag,
        Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemDispatch? pairing,
        IWebSelectedSessionIdentityAuthority? selectedIdentity)
    {
        System.ArgumentNullException.ThrowIfNull(app);
        System.ArgumentNullException.ThrowIfNull(coordinator);
        System.ArgumentNullException.ThrowIfNull(roster);
        System.ArgumentNullException.ThrowIfNull(admitterSigner);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(admitterPartyId);
        System.ArgumentNullException.ThrowIfNull(verifier);
        System.ArgumentNullException.ThrowIfNull(projection);
        System.ArgumentNullException.ThrowIfNull(sodAudit);
        System.ArgumentNullException.ThrowIfNull(activeTeam);

        // ⚠ SECURITY — founder standing is positively asserted for every selected-session web request.
        // Member, unresolved, missing and errored identity reads all refuse before a handler runs. The
        // desktop plane remains unchanged. This admission-only group is deliberately separate from the
        // shared desktop-only group: forms and its other consumers remain blanket-refused on the web plane.
        //
        // HostedAdmissionApiEndpoint captures the admitter party ONCE at startup and this family replays it
        // on every request; nothing here consults IAuthorizationContext, and no admission path is on the
        // listener's pre-auth allowlist. So before this fence a signed-in MEMBER reaching POST /invites,
        // /redeem or /join minted and admitted AS THE GENESIS ADMITTER, signed with the node's key — roster
        // mutation under a borrowed identity, producing an artefact cryptographically INDISTINGUISHABLE
        // from the real admitter's. It was reachable through shipped UI: EnrollmentSection, which hosts the
        // invite and join panels, is mounted in routes/comms.tsx, and Comms is in the nav.
        //
        // The fence goes HERE, at the single funnel both public overloads route through, rather than at the
        // caller. The caller has TWO mapping branches (with and without the pairing bundle) and production
        // takes the one a test harness is least likely to construct, so a fence applied there is only ever
        // proven for whichever branch the harness happened to build. Applied here it covers both branches,
        // all four routes, and any route a later edit adds below — every Map* call takes the group as its
        // builder, so a new route cannot be mapped outside it without changing this line.
        //
        // The /identity read goes in too. The family is one authority surface, and future routes inherit
        // the same standing assertion by mapping on this group. Real member-authority admission is MTW-3.
        app = app.MapFounderWebAdmissionGroup(selectedIdentity);

        // ADDITIVE DEV DIAGNOSTICS (default-on pre-release) — null ⇒ the disabled no-op instance. Never affects the
        // route's HTTP behaviour: the wire response stays opaque; the diagnostic surfaces the real reason in the
        // console only.
        var commsDiag = diag ?? CommsDiagnostics.Disabled;


        // #1301 F-1 — the redeem logic is the shared WireEnrollmentAdmitter core (the ONE admit-and-respond
        // sequence the loopback route AND the network pre-trust channel both call — no triplication). The route is
        // now a thin loopback wrapper that maps the AdmitOutcome to HTTP results.
        var admitter = new Harborline.Api.LocalNodeHost.Enrollment.WireEnrollmentAdmitter(
            coordinator, roster, admitterSigner, admitterPartyId, verifier, projection, sodAudit, activeTeam, commsDiag);

        MapNodeIdentity(app, roster, admitterSigner, activeTeam, commsDiag);
        MapGenerateInvite(app, coordinator, roster);
        MapRedeemInvite(app, admitter, pairing, commsDiag);

        // The B-SIDE join TRIGGER (cerebrum [2026-06-21] gap #1). Mapped only when the joiner orchestrator is wired
        // (a node configured to JOIN a peer team). A node that only HOSTS/admits has no join service → no /join
        // route. The Harborline App invite-entry UI (FED follow-on) calls this; the headless test drives it too.
        if (joinService is not null)
        {
            MapJoin(app, joinService, commsDiag);
        }
    }

    // ── GET /api/local-node/admission/identity — local node's own admission identity ─────────────────────────
    private static void MapNodeIdentity(
        IEndpointRouteBuilder app,
        NodeTeamRoster roster,
        IOperationSigner admitterSigner,
        IActiveTeamAccessor activeTeam,
        CommsDiagnostics diag)
    {
        // GET /api/local-node/admission/identity
        // Returns this node's OWN (partyId, principalPublicKey, transportPublicKey) — the three values a JOINING
        // node presents in the redeem-invite request. The Harborline App UI fetches this instead of faking/supplying the
        // keys client-side (the no-impersonation line: the node is the sole source of truth for its own identity).
        //
        // Security posture:
        //   · Loopback-bound (SharedHostedWebApp.ConfigureUrls → 127.0.0.1 only).
        //   · Gated by the inc-4 listener-level caller-auth (tokenless → 401, same as every other admission route).
        //   · Returns PUBLIC keys only — the private signing key and the private transport key never leave the host.
        //   · No audit side-effect (read-only; does NOT mutate the roster or the durable store).
        //
        // partyId:             the genesis/self party id (os:<user>#<key8>), per-node-distinct.
        // principalPublicKey:  the node's Ed25519 PRINCIPAL public key, base64url (the comms-attribution key).
        // transportPublicKey:  the ACTIVE TEAM's team-scoped TRANSPORT public key, base64url — HKDF(node-root,
        //                      activeTeamId) per DefaultTeamServiceRegistrar. THE #1296 F2 CORRECTNESS PIVOT: this
        //                      MUST be the team-scoped subkey for the team being JOINED (= this node's active team
        //                      after it adopts the team genesis), because that is the EXACT key this node presents
        //                      in the sync HELLO (LocalNodeWorker.WireCrossMachineSyncAsync reads the SAME per-team
        //                      INodeIdentityProvider.Current.PublicKey). The admitting node records this value in
        //                      its roster on redeem, so the joined team's MemberSetTrustPolicy will then trust this
        //                      node's handshake. Returning the root/own-team key instead would make the recorded
        //                      key mismatch the handshake → the two nodes would never trust each other for sync.
        app.MapGet($"{RouteBase}/identity", () =>
        {
            var partyId = roster.Current.GenesisPartyId;
            var principalPublicKey = admitterSigner.IssuerId.ToBase64Url();

            // Resolve the team-scoped transport identity from the ACTIVE TEAM's child container — the ONLY place
            // the HKDF(node-root, teamId) subkey is registered (the registrar), and the same source the wire HELLO
            // uses. Resolved per request so a team-switch / post-bootstrap materialization is reflected live.
            var teamIdentity = activeTeam.Active?.Services.GetService<INodeIdentityProvider>();
            if (teamIdentity is null)
            {
                // No active team materialized yet (pre-bootstrap window) — the node has no team-scoped transport
                // key to present, so a joiner cannot yet be wired into a team's trust set. Fail-closed with 503
                // rather than returning a wrong/absent key the handshake would later reject.
                diag.RouteRejectMapping("GET /admission/identity", "team_not_ready");
                return Results.Json(
                    new { error = "team_not_ready",
                        detail = "No active team is materialized yet; the team-scoped transport identity is not "
                            + "available. Retry once the node has finished team bootstrap." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var transportPublicKey = System.Convert.ToBase64String(teamIdentity.Current.PublicKey)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return Results.Ok(new NodeIdentityResponse(partyId, principalPublicKey, transportPublicKey));
        });
    }

    // ── POST /api/local-node/admission/invites — generate-invite ────────────────────────────────────────────
    private static void MapGenerateInvite(
        IEndpointRouteBuilder app, AdmissionCoordinator coordinator, NodeTeamRoster roster)
    {
        app.MapPost($"{RouteBase}/invites", (GenerateInviteBody? body) =>
        {
            // The trust anchor is published off the CURRENT live roster (genesis party + key). A malformed roster
            // (no bound genesis) cannot publish an anchor — FromRoster throws; surface a 409 rather than a 500.
            TeamTrustAnchor anchor;
            try
            {
                anchor = TeamTrustAnchor.FromRoster(roster.Current);
            }
            catch (System.InvalidOperationException)
            {
                return Results.Conflict(new { error = "no_publishable_anchor" });
            }

            // Optional caller-supplied TTL (minutes); the coordinator defaults to a deliberately-short window when
            // null. A non-positive TTL is rejected by AdmissionToken.Mint — surface it as a 400.
            System.TimeSpan? ttl = body?.TtlMinutes is { } m ? System.TimeSpan.FromMinutes(m) : null;
            AdmissionToken token;
            try
            {
                token = coordinator.CreateInvite(anchor, ttl);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "invalid_ttl", detail = "An invite TTL must be positive." });
            }

            return Results.Ok(new GenerateInviteResponse(
                TokenId: token.TokenId,
                TeamId: anchor.TeamId,
                GenesisPartyId: anchor.GenesisPartyId,
                GenesisPublicKey: anchor.GenesisPublicKey,
                ExpiresAt: token.ExpiresAt.ToString("O")));
        });
    }

    // ── POST /api/local-node/admission/redeem — redeem-invite + admit ───────────────────────────────────────
    // #1301 F-1: this is now a THIN LOOPBACK WRAPPER over the shared WireEnrollmentAdmitter core (the SAME core
    // the network pre-trust channel calls). The loopback route stays for same-node / local-app use; the
    // cross-machine path uses the 7473 network channel. The admit-and-respond logic — verify → invite-redeem +
    // admit → wire trust → publish → SoD audit → build bootstrap — lives ONCE in WireEnrollmentAdmitter.
    private static void MapRedeemInvite(
        IEndpointRouteBuilder app,
        Harborline.Api.LocalNodeHost.Enrollment.WireEnrollmentAdmitter admitter,
        Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemDispatch? pairing,
        CommsDiagnostics diag)
    {
        app.MapPost($"{RouteBase}/redeem", async (RedeemInviteBody? body, HttpContext httpContext, CancellationToken ct) =>
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.TokenId)
                || string.IsNullOrWhiteSpace(body.JoiningPartyId)
                || string.IsNullOrWhiteSpace(body.JoiningPublicKey)
                || string.IsNullOrWhiteSpace(body.JoiningTransportKey)
                || string.IsNullOrWhiteSpace(body.Signature))
            {
                return Results.BadRequest(new { error = "missing_field",
                    detail = "token_id, joining_party_id, joining_public_key, joining_transport_key, and signature "
                        + "are required (the signed two-sided wire-enrollment request)." });
            }

            // Reconstruct the signed enrollment request from the body fields. The shared core (re)verifies the
            // signature + (re)parses the keys + admits — the route does not duplicate that. #3167: the DM + X-Wing
            // PUBLIC keys are now carried so the reconstructed request matches the joiner's SIGNED transcript (which
            // covers them) — else VerifyRequest would fail. Absent (a legacy plain enrollment) ⇒ empty, byte-stable.
            var enrollmentRequest = new EnrollmentRequest(
                TokenId: body.TokenId,
                JoiningPartyId: body.JoiningPartyId,
                JoiningTransportPublicKey: body.JoiningTransportKey,
                JoiningPrincipalPublicKey: body.JoiningPublicKey,
                IssuedAt: System.DateTimeOffset.FromUnixTimeMilliseconds(body.IssuedAtUnixMs ?? 0),
                Nonce: System.Guid.TryParse(body.Nonce, out var parsedNonce) ? parsedNonce : System.Guid.Empty,
                Signature: body.Signature,
                JoiningDmPublicKey: body.JoiningDmKey ?? string.Empty,
                JoiningXWingPublicKey: body.JoiningXWingKey ?? string.Empty);

            // #3167 — mode-exclusive dispatch (R2). When the pairing path is wired:
            //   · binding present → the token-gated PAIRING admitter (R1.3 + R4-a).
            //   · binding absent + web-plane admission ENABLED for the tenant → opaque refuse (the plain path is
            //     mode-exclusively OFF; F4-a — one byte-identical opaque reply).
            //   · else → the plain WireEnrollmentAdmitter (open-enrollment posture).
            // When pairing is NOT wired (legacy/plain-only host), skip straight to the plain path (unchanged).
            if (pairing is not null)
            {
                // #3167 M3 — the ENTIRE pairing pre-dispatch (tenant resolve + rate-limit + binding lookup +
                // web-plane predicate + pairing admit) is CONTAINED: a durable-store fault in ANY of these
                // route-level calls collapses to the SAME opaque refusal, never an escaping 500 (R6/C extended from
                // the admitter to the route layer). A non-null result IS the response (pairing success, opaque
                // refuse, or the 429 throttle); a null result means "fall through to the open plain path".
                var pairingResult = await TryPairingDispatchAsync(
                    pairing, enrollmentRequest, httpContext, diag, ct).ConfigureAwait(false);
                if (pairingResult is not null)
                {
                    return pairingResult;
                }
            }

            // PLAIN path (open-enrollment posture) — the shared WireEnrollmentAdmitter core, unchanged.
            var outcome = await admitter.AdmitAsync(enrollmentRequest, ct).ConfigureAwait(false);
            if (!outcome.Accepted || outcome.Response is null)
            {
                // Map the coarse fail-closed reject reason to an HTTP status. The reasons are opaque (they never
                // disclose which of replayed/expired/unknown), preserving the prior route's no-leak posture. The
                // ADDITIVE diagnostic surfaces the real reason in the console only (the wire stays opaque).
                diag.RouteRejectMapping("POST /admission/redeem", outcome.RejectReason);
                return outcome.RejectReason switch
                {
                    "team_not_ready" => Results.Json(
                        new { error = "team_not_ready",
                            detail = "The admission committed but the team-scoped transport identity is not yet "
                                + "available to build the enrollment response; retry once the node finishes team bootstrap." },
                        statusCode: StatusCodes.Status503ServiceUnavailable),
                    "admit_rejected" => Results.Conflict(new { error = "admit_rejected" }),
                    _ => Results.BadRequest(new { error = outcome.RejectReason ?? "invite_rejected" }),
                };
            }

            return Results.Ok(outcome.Response);
        });
    }

    /// <summary>
    /// #3167 (R2 dispatch + R5 rate-limit + M3 containment) — run the pairing pre-dispatch. Returns the response to
    /// send (pairing success / opaque refuse / 429 throttle), or <c>null</c> to fall through to the open plain path
    /// (binding absent AND web-plane admission disabled for the tenant). EVERY fault — a durable-store throw
    /// (locked SQLite, disk error, the partial-migration missing-table SqliteException) in the tenant resolve,
    /// binding lookup, or web-plane predicate — is CONTAINED to the SAME opaque refusal
    /// (<see cref="PairingWireOutcome.OpaqueRefusal"/>), never an escaping 500 (R6/C, extended from the admitter to
    /// this route layer — verdict M3). A fault is fail-CLOSED (opaque refuse, NOT a plain-path fall-through), so a
    /// binding-store outage can never silently reopen the plain path when web-plane admission is enabled.
    /// <see cref="OperationCanceledException"/> propagates (cancellation is real).
    /// </summary>
    private static async Task<IResult?> TryPairingDispatchAsync(
        Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemDispatch pairing,
        EnrollmentRequest enrollmentRequest,
        HttpContext httpContext,
        CommsDiagnostics diag,
        CancellationToken ct)
    {
        var source = PairingRateLimitSource(httpContext);
        var outcome = await pairing.DispatchAsync(enrollmentRequest, source, ct).ConfigureAwait(false);
        switch (outcome.Kind)
        {
            case PairingDispatchKind.RateLimited:
                diag.RouteRejectMapping("POST /admission/redeem", "rate_limited");
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            case PairingDispatchKind.Refused:
                diag.RouteRejectMapping("POST /admission/redeem (pairing)", outcome.InternalReason);
                return PairingWireOutcome.OpaqueRefusal().Result;
            case PairingDispatchKind.Accepted:
                return PairingWireOutcome.Success(outcome.Response!).Result;
            case PairingDispatchKind.PlainPath:
                return null;
            default:
                throw new InvalidOperationException($"Unknown pairing dispatch outcome '{outcome.Kind}'.");
        }
    }

    /// <summary>
    /// L3 — derive the per-source throttle key. A loopback IP identifies the whole machine, not the authenticated
    /// caller, so local requests key on the selected-session principal or a one-way fingerprint of the bootstrap
    /// bearer. Non-loopback callers remain keyed on network source. The bearer itself is never retained or logged.
    /// </summary>
    internal static string PairingRateLimitSource(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            return $"ip:{remote}";
        }

        var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
        if (principal is not null)
        {
            return $"principal:{principal.TenantId.Value}:{principal.PrincipalUserId.Value}";
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(authorization[bearerPrefix.Length..]))
        {
            var bearer = authorization[bearerPrefix.Length..].Trim();
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(bearer));
            return $"caller:{Convert.ToHexString(digest.AsSpan(0, 16))}";
        }

        return remote is null ? "unknown" : $"ip:{remote}";
    }

    /// <summary>
    /// #3167 F4-a — the TYPE-ENFORCED wire outcome for the pairing / mode-exclusive branches. It has exactly TWO
    /// factories: <see cref="Success"/> (the bootstrap response) and <see cref="OpaqueRefusal"/> (the ONE opaque
    /// reply EVERY refusal collapses to — identical HTTP status + body for every internal cause). There is no
    /// third factory, so the pairing branch is STRUCTURALLY unable to emit a distinguishable refusal (the
    /// content/status enumeration-oracle closure the ruling requires at the route).
    /// </summary>
    private readonly struct PairingWireOutcome
    {
        private readonly IResult _result;
        private PairingWireOutcome(IResult result) => _result = result;

        public IResult Result => _result;

        public static PairingWireOutcome Success(EnrollmentResponse response) => new(Results.Ok(response));

        public static PairingWireOutcome OpaqueRefusal() =>
            new(OpaqueEnrollmentRefusalResult.Instance);
    }

    // ── POST /api/local-node/admission/join — the B-SIDE JOIN trigger ───────────────────────────────────────
    //
    // gap #1 (cerebrum [2026-06-21]): the PRODUCTION trigger for the joiner half. THIS node (B) is the one JOINING;
    // it dials the ADMITTER (A) over the wire, enrolls from the invite alone, adopts A's team, and switches its
    // active team to A's — which rebinds B's gossip daemon to its A-team HELLO key (the daemon-rebind). The body
    // carries the invite token id, the web-bound Party, and the team trust anchor B received OUT-OF-BAND. The Party
    // is covered by B's enrollment signature and verified against the pairing token by A.
    // by the wired IEnrollmentTransport (so the operator/peer-config supplies it, not the request body — the
    // address is install config, the invite is the per-join secret).
    //
    // SECURITY: this is a LOCAL CONTROL, not the pre-trust network surface. It is F1-caller-auth'd exactly like
    // /redeem + /invites (NOT in CallerAuthAllowlist) — B's local Harborline App triggers it; a local stranger process is
    // rejected 401. The actual cross-machine trust is still the #1301 invite-gated 7473 exchange this route DRIVES;
    // no trust is conferred by reaching this route, only by a valid invite redeemed by A over the wire. Fail-closed:
    // a rejected enroll leaves B on its own team (no team-switch, no rebind).
    private static void MapJoin(
        IEndpointRouteBuilder app,
        Harborline.Api.LocalNodeHost.Enrollment.NodeEnrollmentJoinService joinService,
        CommsDiagnostics diag)
    {
        app.MapPost($"{RouteBase}/join", async (JoinTeamBody? body, CancellationToken ct) =>
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.TokenId)
                || string.IsNullOrWhiteSpace(body.JoiningPartyId)
                || string.IsNullOrWhiteSpace(body.TeamId)
                || string.IsNullOrWhiteSpace(body.GenesisPartyId)
                || string.IsNullOrWhiteSpace(body.GenesisPublicKey))
            {
                return Results.BadRequest(new { error = "missing_field",
                    detail = "token_id, joining_party_id, team_id, genesis_party_id, and genesis_public_key are required "
                        + "(the invite token + the out-of-band team trust anchor)." });
            }

            var anchor = new TeamTrustAnchor(
                TeamId: body.TeamId!,
                GenesisPartyId: body.GenesisPartyId!,
                GenesisPublicKey: body.GenesisPublicKey!);

            JoinOutcome outcome;
            try
            {
                outcome = await joinService.JoinAsync(body.TokenId!, body.JoiningPartyId!, anchor, ct)
                    .ConfigureAwait(false);
            }
            catch (System.InvalidOperationException ex)
            {
                // A wiring fault (e.g. neither AdmitterSyncEndpoint nor AdmitterUrl configured — the transport
                // fails closed at call time) surfaces as a 409 with a coarse, PII-free reason. The roster/genesis
                // state is never disclosed (the anchor the caller supplied is public anyway).
                diag.RouteRejectMapping("POST /admission/join", "join_not_configured");
                return Results.Conflict(new { error = "join_not_configured", detail = ex.Message });
            }

            if (!outcome.Succeeded)
            {
                // The enroll was rejected fail-closed (bad/expired/replayed invite, no response, validation
                // failure). Opaque reason — never disclose which. The node is unchanged. The ADDITIVE diagnostic
                // surfaces the real reason in the console only (the wire stays opaque).
                diag.RouteRejectMapping("POST /admission/join", outcome.FailureReason);
                return Results.BadRequest(new { error = outcome.FailureReason ?? "join_rejected" });
            }

            return Results.Ok(new JoinTeamResponse(Joined: true, TeamId: outcome.TeamId.ToString("D")));
        });
    }

    // ── Request bodies ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>generate-invite body — optional caller-supplied TTL (minutes). Null ⇒ the coordinator default.</summary>
    public sealed record GenerateInviteBody(double? TtlMinutes);

    /// <summary>
    /// redeem-invite body — the SIGNED two-sided wire-enrollment request (cerebrum [2026-06-21]). The joiner
    /// presents the invite token id + its identity (party id, principal/comms-author public key, team-scoped
    /// TRANSPORT public key it presents in the sync HELLO — all base64url) PLUS the signature over that binding
    /// (with the issuance instant + nonce that complete the signed envelope). A verifies the signature under the
    /// claimed principal key before admitting (proof-of-possession; defeats a MITM key-swap).
    /// </summary>
    /// <remarks>
    /// #3167 — <c>JoiningDmKey</c> (base64url X25519, 32-byte) and <c>JoiningXWingKey</c> (base64url X-Wing,
    /// 1216-byte) are ADDITIVE optional fields. The joiner's signed transcript COVERS them (R4-a), so when the
    /// device presents them the route MUST carry them for the reconstructed request to re-verify. On the web-admitted
    /// PAIRING path the X-Wing key is MANDATORY (the pairing admitter refuses an absent/malformed one); on the plain
    /// path both are optional (absent ⇒ empty, byte-stable — a legacy joiner presents neither).
    /// </remarks>
    public sealed record RedeemInviteBody(
        string? TokenId,
        string? JoiningPartyId,
        string? JoiningPublicKey,
        string? JoiningTransportKey,
        string? Signature,
        long? IssuedAtUnixMs,
        string? Nonce,
        string? JoiningDmKey = null,
        string? JoiningXWingKey = null);

    // ── Response DTOs ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>generate-invite response — the token id + the public team trust anchor + the expiry instant.</summary>
    public sealed record GenerateInviteResponse(
        string TokenId,
        string TeamId,
        string GenesisPartyId,
        string GenesisPublicKey,
        string ExpiresAt);

    /// <summary>redeem-invite response — the admitted party + team on success.</summary>
    public sealed record RedeemInviteResponse(
        bool Admitted,
        string PartyId,
        string TeamId);

    /// <summary>
    /// join-team body — the B-side join trigger (gap #1). The invite token id + its web-bound Party + the team trust
    /// anchor B received OUT-OF-BAND (team id + genesis party id + genesis pubkey, base64url). The admitter's reachable address is
    /// NOT in the body — it is read from <c>LocalNode:Enrollment:AdmitterSyncEndpoint</c> by the wired transport
    /// (the address is install config; the invite is the per-join secret).
    /// </summary>
    public sealed record JoinTeamBody(
        string? TokenId,
        string? JoiningPartyId,
        string? TeamId,
        string? GenesisPartyId,
        string? GenesisPublicKey);

    /// <summary>join-team response — the joined team id on success (B's active team is now this team).</summary>
    public sealed record JoinTeamResponse(
        bool Joined,
        string TeamId);

    /// <summary>
    /// node-identity response — the local node's OWN admission identity, returned by
    /// <c>GET /api/local-node/admission/identity</c>. A JOINING node fetches this to obtain the
    /// (party id, principal pubkey, transport pubkey) it presents in the redeem-invite request.
    /// The Harborline App fetches these; they are NEVER faked or accepted from the Harborline App side
    /// (the no-impersonation line).
    /// </summary>
    public sealed record NodeIdentityResponse(
        string PartyId,
        string PrincipalPublicKey,
        string TransportPublicKey);
}
