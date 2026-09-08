using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Diagnostics;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The B-SIDE JOIN ORCHESTRATOR (cerebrum [2026-06-21] — "the B-side join completion"). This is the PRODUCTION
/// TRIGGER for the joiner half of two-sided wire enrollment: it drives <see cref="NodeWireEnrollmentClient.EnrollAsync"/>
/// over the wired transport, then performs the two follow-on steps the bare client cannot (it owns no team
/// lifecycle): it MATERIALIZES + activates the adopted team and SWITCHES the node's ACTIVE TEAM to it — which, via
/// the worker's team-switch subscription, REBINDS the gossip daemon to B's A-team-scoped HELLO key. After this
/// returns success, B presents the A-team key on the trusted HELLO, A trusts it (A recorded the same key at
/// admission), and the handshake that previously failed <c>PEER_UNTRUSTED</c> from B's side now PASSES.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a service, not inline in the route.</b> The join is THREE coordinated steps across three subsystems —
/// the enrollment protocol client (foundation), the team-context factory + store activator + active-team accessor
/// (kernel-runtime), and (transitively, via <see cref="IActiveTeamAccessor.ActiveChanged"/>) the gossip daemon
/// lifecycle (the worker). Keeping the sequence in one orchestrator makes it (a) testable end-to-end without HTTP
/// and (b) the single seam the loopback <c>POST /admission/join</c> route and the Harborline App invite-entry UI (FED
/// follow-on) both call. The route is a thin HTTP wrapper over <see cref="JoinAsync"/>.
/// </para>
/// <para>
/// <b>The sequence (fail-closed; adopts nothing on any failure).</b>
/// <list type="number">
///   <item><b>EnrollAsync</b> — B derives its A-team transport key, sends the signed enroll request over the
///     transport (the cross-machine 7473 socket or loopback HTTP fallback), validates A's response against the
///     out-of-band invite anchor, and — only on full validation — ADOPTS A's roster + trust map into B's
///     install-level <see cref="NodeTeamRoster"/>. A failed enroll returns the fail-closed reason and the node is
///     UNCHANGED (B is still on its own genesis team; no team-switch, no daemon-rebind).</item>
///   <item><b>Materialize + activate A's team</b> — <see cref="ITeamContextFactory.GetOrCreateAsync"/> builds the
///     per-team child container for A.teamId (its <c>INodeIdentityProvider</c> = HKDF(B-root, A.teamId), its
///     <c>MemberSetTrustPolicy</c> reads the now-adopted roster, its enrollment handler is wired) and
///     <see cref="ITeamStoreActivator.ActivateAsync"/> opens its encrypted store. This is the same materialize +
///     activate the bootstrap hosted service does for a configured team — so the adopted team is a first-class
///     materialized team, not a special case.</item>
///   <item><b>SetActive(A.teamId)</b> — flips <see cref="IActiveTeamAccessor.Active"/> to A's team. This fires
///     <see cref="IActiveTeamAccessor.ActiveChanged"/>, which the worker observes to STOP the old team's daemon
///     and START A's team's daemon (the DAEMON-REBIND). B now presents the A-team HELLO key.</item>
/// </list>
/// </para>
/// <para>
/// <b>The active-team-switch is what re-keys B.</b> Before the switch, B's daemon was bound at boot to B's own
/// genesis team (it presented B's own-team subkey HKDF(B-root, B.genesisTeamId)). A recorded B's <em>A-team</em>
/// subkey at admission (the #1296-F2 JOINED-team scope). So before the switch B's HELLO key ≠ the key A trusts →
/// PEER_UNTRUSTED. After the switch the active team is A.teamId, so the per-team identity provider B's rebound
/// daemon resolves derives HKDF(B-root, A.teamId) — byte-identical to what A recorded → trusted. Step 3 is the
/// load-bearing one; steps 1+2 set it up.
/// </para>
/// <para>
/// <b>Single-user unchanged.</b> This orchestrator runs ONLY when the operator drives a join (the route is
/// invoked). A node that never joins another team never switches its active team and its daemon stays bound to its
/// genesis team exactly as before — the team-switch + rebind paths are dormant.
/// </para>
/// </remarks>
public sealed class NodeEnrollmentJoinService
{
    private readonly NodeWireEnrollmentClient _client;
    private readonly IEnrollmentTransport _transport;
    private readonly ITeamContextFactory _teamContextFactory;
    private readonly ITeamStoreActivator _storeActivator;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<NodeEnrollmentJoinService>? _logger;
    private readonly CommsDiagnostics _diag;

    /// <summary>Construct the join orchestrator over the wired joiner client + transport + team lifecycle seams.</summary>
    public NodeEnrollmentJoinService(
        NodeWireEnrollmentClient client,
        IEnrollmentTransport transport,
        ITeamContextFactory teamContextFactory,
        ITeamStoreActivator storeActivator,
        IActiveTeamAccessor activeTeam,
        ILogger<NodeEnrollmentJoinService>? logger = null,
        CommsDiagnostics? diag = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _teamContextFactory = teamContextFactory ?? throw new ArgumentNullException(nameof(teamContextFactory));
        _storeActivator = storeActivator ?? throw new ArgumentNullException(nameof(storeActivator));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _logger = logger;
        // ADDITIVE DEV DIAGNOSTICS (default-on pre-release): observe the join decision below; never affects control
        // flow. Null ⇒ the disabled no-op instance.
        _diag = diag ?? CommsDiagnostics.Disabled;
    }

    /// <summary>
    /// JOIN A's team from an invite: enroll over the wire, adopt A's team, materialize + activate it, and switch
    /// the active team to it (which rebinds the gossip daemon to B's A-team key). Returns a fail-closed
    /// <see cref="JoinOutcome"/>: the adopted team id on success, or the reason it failed. On ANY failure the node
    /// is left on its prior active team (no partial switch).
    /// </summary>
    /// <param name="tokenId">The invite token id B presents (delivered out-of-band with the anchor).</param>
    /// <param name="inviteAnchor">The team trust anchor B received out-of-band — the trust pin + the source of
    /// A.teamId B scopes its transport key to.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<JoinOutcome> JoinAsync(string tokenId, TeamTrustAnchor inviteAnchor, CancellationToken ct)
        => await JoinAsync(tokenId, joiningPartyId: null, inviteAnchor, ct).ConfigureAwait(false);

    /// <summary>Join while presenting the web-bound Party admitted by the local join route.</summary>
    public async Task<JoinOutcome> JoinAsync(
        string tokenId,
        string? joiningPartyId,
        TeamTrustAnchor inviteAnchor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentNullException.ThrowIfNull(inviteAnchor);

        // (1) Enroll over the wire — derive B's A-team key, send the signed request, validate A's response, adopt
        //     A's roster + trust map. Fail-closed: a rejected enroll changes nothing on this node.
        var enrollment = string.IsNullOrWhiteSpace(joiningPartyId)
            ? await _client.EnrollAsync(tokenId, inviteAnchor, _transport, ct).ConfigureAwait(false)
            : await _client.EnrollAsync(tokenId, joiningPartyId, inviteAnchor, _transport, ct).ConfigureAwait(false);
        if (!enrollment.Succeeded)
        {
            _logger?.LogWarning(
                "Wire-enrollment JOIN rejected at the enroll step ({Reason}) — node UNCHANGED (no team-switch, "
                + "no daemon-rebind).", enrollment.FailureReason);
            // ADDITIVE DEV DIAGNOSTICS — surface the REAL joiner-side reason + the team B tried to join (the route
            // flattens this to a coarse status).
            _diag.JoinOutcome(succeeded: false, enrollment.FailureReason, inviteAnchor.TeamId);
            return JoinOutcome.Failed(enrollment.FailureReason ?? "enroll_failed");
        }

        var adoptedTeamId = new TeamId(enrollment.TeamId);
        _diag.TeamAdoptionStep("enroll-validated", inviteAnchor.TeamId, "A's response validated against the invite "
            + "anchor; adopting A's roster + trust map.");

        // (2) Materialize + activate the adopted team so its per-team child container exists (with B's A-team
        //     identity + the trust policy reading the now-adopted roster + the enrollment handler) and its store is
        //     open — the prerequisite for SetActiveAsync (which refuses an un-materialized team).
        var displayName = $"Team {enrollment.TeamId:D}";
        await _teamContextFactory.GetOrCreateAsync(adoptedTeamId, displayName, ct).ConfigureAwait(false);
        await _storeActivator.ActivateAsync(adoptedTeamId, ct).ConfigureAwait(false);
        _diag.TeamAdoptionStep("materialize+activate", enrollment.TeamId.ToString("D"),
            "per-team child container built + encrypted store opened.");

        // (3) Switch the active team to A's team. This fires IActiveTeamAccessor.ActiveChanged, which the worker
        //     observes to STOP the prior team's daemon and START A's team's daemon — the DAEMON-REBIND. B's rebound
        //     daemon resolves the A-team INodeIdentityProvider (HKDF(B-root, A.teamId)), so it now presents the
        //     exact HELLO key A recorded for B at admission → the trusted handshake passes.
        await _activeTeam.SetActiveAsync(adoptedTeamId, ct).ConfigureAwait(false);
        _diag.TeamAdoptionStep("active-team-switch", enrollment.TeamId.ToString("D"),
            "active team flipped → gossip daemon rebinds to this node's A-team HELLO key (the re-key that makes the "
            + "trusted handshake pass).");

        _logger?.LogInformation(
            "Wire-enrollment JOIN succeeded: adopted team {TeamId}; active team switched → the gossip daemon "
            + "rebinds to this node's A-team HELLO key (the trusted handshake with the admitter now passes).",
            enrollment.TeamId);
        _diag.JoinOutcome(succeeded: true, reason: null, enrollment.TeamId.ToString("D"));

        return JoinOutcome.Joined(enrollment.TeamId);
    }
}

/// <summary>The outcome of a B-side JOIN — the adopted team id on success, or the fail-closed reason. On failure
/// the node was left on its prior active team (no team-switch, no daemon-rebind).</summary>
public sealed record JoinOutcome
{
    private JoinOutcome(bool succeeded, string? failureReason, Guid teamId)
    {
        Succeeded = succeeded;
        FailureReason = failureReason;
        TeamId = teamId;
    }

    /// <summary>True iff B enrolled, adopted A's team, and switched its active team to it.</summary>
    public bool Succeeded { get; }

    /// <summary>The fail-closed reason when not succeeded; otherwise null.</summary>
    public string? FailureReason { get; }

    /// <summary>The team id B joined (= A.teamId) on success; <see cref="Guid.Empty"/> on failure.</summary>
    public Guid TeamId { get; }

    internal static JoinOutcome Joined(Guid teamId) => new(true, null, teamId);

    internal static JoinOutcome Failed(string reason) => new(false, reason, Guid.Empty);
}
