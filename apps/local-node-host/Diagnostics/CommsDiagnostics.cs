using System;

using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Diagnostics;

/// <summary>
/// The DEV / troubleshooting COMMS DIAGNOSTIC LOGGER — the single seam that makes the REAL comms / enrollment /
/// peer-trust / roster reason plainly VISIBLE in the node console while the product is in PRE-RELEASE development.
/// Gated by the <c>LocalNode:Diagnostics:CommsDiagnosticLogging</c> flag (DEFAULT ON for dev; flips OFF for the
/// production release). Every line carries the <c>[comms-diag]</c> prefix so it stands out in the tauri:dev node
/// terminal where the operator reads the sidecar's stdout.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem it fixes.</b> The Harborline App flattened admission reject reasons to a generic catch-all and node-trust
/// decisions (<c>PEER_UNTRUSTED</c>, roster adopt-vs-refuse, key-scope mismatch) were invisible — so a join/sync
/// failure surfaced as an opaque "rejected" with the real reason hidden, costing long blind debugging slogs. This
/// logger emits the real reason + side + the (PUBLIC) identifiers needed to diagnose it, at the exact points that
/// previously hid it.
/// </para>
/// <para>
/// <b>SECURITY — never log a secret or opaque capability identifier.</b> Every emit method takes ONLY non-secret
/// inputs: lengths, teamIds, party ids (already public), boolean decisions, and scope names. It NEVER accepts or
/// formats a key, token id, token, seed, signature, or any private material. The surface deliberately has no token-id
/// parameter, so a future caller cannot accidentally reintroduce the pre-GA diagnostic correlation handle.
/// </para>
/// <para>
/// <b>Behaviour-neutral.</b> When the flag is ON the methods log; when OFF they are no-ops. Either way they have NO
/// side effect on enrollment / admission / trust CONTROL FLOW — they observe a decision the caller already made and
/// return <c>void</c>. The flag gates VERBOSITY, never secrecy and never behaviour.
/// </para>
/// </remarks>
public sealed class CommsDiagnostics
{
    /// <summary>The structured <c>ILogger</c> category prefix every diagnostic line carries.</summary>
    public const string Prefix = "[comms-diag]";

    private readonly ILogger? _logger;
    private readonly bool _enabled;

    /// <summary>
    /// Construct over the host logger + the resolved <c>CommsDiagnosticLogging</c> flag value. A null logger (a
    /// minimal DI test) makes every emit a no-op even when enabled — the helper never throws on a missing logger.
    /// </summary>
    /// <param name="logger">The host logger the diagnostic lines are written to (the node stdout in dev).</param>
    /// <param name="enabled">The resolved <c>LocalNode:Diagnostics:CommsDiagnosticLogging</c> flag (default ON).</param>
    public CommsDiagnostics(ILogger? logger, bool enabled)
    {
        _logger = logger;
        _enabled = enabled;
    }

    /// <summary>A disabled instance (the OFF posture) — every emit is a no-op. Used for the production-release path
    /// and as a never-null fallback.</summary>
    public static CommsDiagnostics Disabled { get; } = new(null, enabled: false);

    /// <summary>True iff the flag is ON AND a logger is wired — the verbose lines are emitted.</summary>
    public bool IsEnabled => _enabled && _logger is not null;

    // ── Enrollment / admission outcome diagnostics ────────────────────────────────────────────────────────────

    /// <summary>
    /// Emit the A-SIDE admit outcome (the admitter's view) — the REAL reject reason or the admitted party. This is
    /// the #1 hidden-reason point: the route flattens the admitter's coarse reject reason to an opaque HTTP status,
    /// so without this the real reason
    /// (<c>enroll_signature_invalid</c> / <c>invalid_key</c> / <c>admit_rejected</c> / <c>invite_rejected</c> /
    /// <c>team_not_ready</c>) never reaches the console.
    /// </summary>
    public void AdmitOutcome(bool accepted, string? reason, string? joiningPartyId)
    {
        if (!IsEnabled) return;
        if (accepted)
        {
            _logger!.LogInformation(
                "{Prefix} admit ACCEPTED side=admitter joiningParty={Party} — the joiner is "
                + "admitted into the roster + wired into transport-trust.",
                Prefix, joiningPartyId ?? "<none>");
        }
        else
        {
            _logger!.LogWarning(
                "{Prefix} admit REJECTED side=admitter reason={Reason} joiningParty={Party} — "
                + "the REAL fail-closed reason (the HTTP route flattens this to an opaque status).",
                Prefix, reason ?? "<unspecified>", joiningPartyId ?? "<none>");
        }
    }

    /// <summary>
    /// Emit the B-SIDE join outcome (the joiner's view) — the REAL fail reason or the joined team. The
    /// <see cref="Harborline.Api.LocalNodeHost.Enrollment.NodeWireEnrollmentClient"/> / join orchestrator produces reasons
    /// (<c>no_response</c> / <c>validation_failed</c> / <c>anchor_mismatch</c> / <c>team_id_mismatch</c> /
    /// <c>joiner_key_mismatch</c> / <c>roster_supersession_failed</c>) the route then flattens to a coarse
    /// <c>join_rejected</c>.
    /// </summary>
    public void JoinOutcome(bool succeeded, string? reason, string? teamId)
    {
        if (!IsEnabled) return;
        if (succeeded)
        {
            _logger!.LogInformation(
                "{Prefix} join SUCCEEDED side=joiner teamId={TeamId} — adopted A's team + switched "
                + "active team (gossip daemon rebinds to the A-team HELLO key; the trusted handshake now passes).",
                Prefix, teamId ?? "<none>");
        }
        else
        {
            _logger!.LogWarning(
                "{Prefix} join REJECTED side=joiner reason={Reason} teamId={TeamId} — node "
                + "UNCHANGED (no team-switch, no daemon-rebind). The REAL reason the route flattens to a coarse "
                + "status.",
                Prefix, reason ?? "<unspecified>", teamId ?? "<none>");
        }
    }

    /// <summary>
    /// Emit a coarse HTTP admission-route mapping — the real reason the route is about to flatten to an opaque
    /// status, plus which route + which side. Complements PR #1333 (which surfaces the app-side reason) by
    /// making the NODE-side reason visible at the moment of flattening.
    /// </summary>
    public void RouteRejectMapping(string route, string? reason)
    {
        if (!IsEnabled) return;
        _logger!.LogWarning(
            "{Prefix} route {Route} mapping reason={Reason} → an opaque HTTP status (the no-leak posture is "
            + "preserved on the wire; this diagnostic surfaces the real reason in the console only).",
            Prefix, route, reason ?? "<unspecified>");
    }

    // ── Peer-trust decision diagnostics ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emit a peer-trust decision (the <c>PEER_UNTRUSTED</c>-vs-trusted reason). The handshake's trust gate
    /// (<c>MemberSetTrustPolicy</c>) only answers a boolean and rejects with <c>PEER_UNTRUSTED</c> — WHY (the peer's
    /// presented HELLO key is not in this node's trusted-transport set) is invisible. This surfaces the decision +
    /// the size of the trusted set + the presented-key LENGTH (NEVER the key value), the diagnosis for the
    /// genesis-vs-adopted-team scope mismatch.
    /// </summary>
    /// <param name="trusted">The trust decision the gate made.</param>
    /// <param name="presentedKeyLength">The byte LENGTH of the peer's presented HELLO public key (NEVER the value).</param>
    /// <param name="trustedSetCount">How many keys are in this node's trusted-transport set (a count, not the keys).</param>
    public void PeerTrustDecision(bool trusted, int presentedKeyLength, int trustedSetCount)
    {
        if (!IsEnabled) return;
        if (trusted)
        {
            _logger!.LogDebug(
                "{Prefix} peer-trust TRUSTED presentedKeyLen={Len}B trustedSetCount={Count} — the peer's HELLO "
                + "transport key is in this node's trusted set.",
                Prefix, presentedKeyLength, trustedSetCount);
        }
        else
        {
            _logger!.LogWarning(
                "{Prefix} peer-trust PEER_UNTRUSTED presentedKeyLen={Len}B trustedSetCount={Count} — the peer's "
                + "presented HELLO transport key is NOT in this node's trusted set. Common cause: the peer is "
                + "presenting its GENESIS-team-scoped key but this node recorded its ADOPTED-team-scoped key (or "
                + "vice-versa) — a team-scope mismatch (the join hasn't switched the peer's active team yet).",
                Prefix, presentedKeyLength, trustedSetCount);
        }
    }

    // ── Team adoption / key-scoping / roster diagnostics ──────────────────────────────────────────────────────

    /// <summary>
    /// Emit a team-adoption / active-team-switch step — which teamId, what the step did. Makes the supersession /
    /// adoption / active-team-switch sequence (the B-side join's load-bearing re-key) visible.
    /// </summary>
    public void TeamAdoptionStep(string step, string? teamId, string? detail = null)
    {
        if (!IsEnabled) return;
        _logger!.LogInformation(
            "{Prefix} team-adoption step={Step} teamId={TeamId}{Detail}",
            Prefix, step, teamId ?? "<none>", detail is null ? string.Empty : $" — {detail}");
    }

    /// <summary>
    /// Emit a key-scoping diagnostic — the team-scope a DM / transport key was DERIVED under (the teamId fed into the
    /// HKDF) + the key's byte LENGTH (NEVER the key value). This is the diagnosis for the #1296-F2 family: a key
    /// derived under the wrong team-scope is the genesis-vs-adopted mismatch that makes the handshake fail
    /// <c>PEER_UNTRUSTED</c>.
    /// </summary>
    /// <param name="keyKind">A label for which key (e.g. "transport", "dm").</param>
    /// <param name="teamScope">The teamId the key was scoped to (public).</param>
    /// <param name="keyLength">The derived key's byte LENGTH (NEVER the value).</param>
    public void KeyScoping(string keyKind, string? teamScope, int keyLength)
    {
        if (!IsEnabled) return;
        _logger!.LogDebug(
            "{Prefix} key-scoping kind={Kind} teamScope={Scope} keyLen={Len}B — the team-scope this key was "
            + "HKDF-derived under (a wrong scope is the genesis-vs-adopted mismatch behind PEER_UNTRUSTED).",
            Prefix, keyKind, teamScope ?? "<none>", keyLength);
    }

    /// <summary>
    /// Emit an invite token redeem/consume ordering step without any token identifier. Makes the
    /// redeem/consume ordering (redeem-before-admit, single-use enforcement) observable.
    /// </summary>
    public void TokenRedeemStep(string step)
    {
        if (!IsEnabled) return;
        _logger!.LogDebug(
            "{Prefix} token-redeem step={Step}.",
            Prefix, step);
    }

    /// <summary>
    /// Emit a roster-rebuild adopt-vs-refuse outcome (the #1331/#1333 own-membership guard) — adopt or refuse + why.
    /// <c>RosterCrdtProjection</c> already logs these; this mirrors them under the unified <c>[comms-diag]</c> prefix
    /// so the operator can scan one prefix for every comms/enrollment/trust decision.
    /// </summary>
    public void RosterRebuild(bool adopted, string reason, string? teamId = null)
    {
        if (!IsEnabled) return;
        if (adopted)
        {
            _logger!.LogInformation(
                "{Prefix} roster-rebuild ADOPT teamId={TeamId} reason={Reason}",
                Prefix, teamId ?? "<none>", reason);
        }
        else
        {
            _logger!.LogWarning(
                "{Prefix} roster-rebuild REFUSE teamId={TeamId} reason={Reason} — keeping the local roster "
                + "(fail-safe; own-membership guard).",
                Prefix, teamId ?? "<none>", reason);
        }
    }
}
