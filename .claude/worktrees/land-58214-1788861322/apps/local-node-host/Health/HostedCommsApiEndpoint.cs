using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local comms routes onto the shared Kestrel listener — the FIRST
/// messaging doctype's HTTP surface. The messaging analogue of <see cref="HostedContactApiEndpoint"/>.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="CommsRoutes.Map"/> (the single source of truth). The comms projection, the node's canonical
/// signer (the active member's signing identity in this pilot), and the active-team accessor are injected
/// from the OUTER host container and passed to <see cref="CommsRoutes.Map"/> as closed-over dependencies —
/// resolving via <c>[FromServices]</c> inside the route handlers would fail because the routes are mapped
/// onto <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider does NOT have
/// the outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedCommsApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly CommsConversationRegistry _conversations;
    private readonly NodePrincipalSigner _signer;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly NodeTeamRoster _roster;
    private readonly CommsDmFeatureFlag _dmFlag;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedCommsApiEndpoint> _logger;

    /// <summary>Constructs the hosted comms API endpoint.</summary>
    public HostedCommsApiEndpoint(
        SharedHostedWebApp sharedApp,
        CommsConversationRegistry conversations,
        NodePrincipalSigner signer,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        NodeTeamRoster roster,
        CommsDmFeatureFlag dmFlag,
        TimeProvider timeProvider,
        ILogger<HostedCommsApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(dmFlag);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _conversations = conversations;
        _signer = signer;
        _activeTeam = activeTeam;
        _callerAuth = callerAuth;
        _roster = roster;
        _dmFlag = dmFlag;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // gap #2 — the author of every appended message is the ACTIVE ENROLLED MEMBER: the trust-roster party
        // bound to the signing principal key (the seeded genesis on a single-office node). Resolving it here
        // from the SAME seeded NodeTeamRoster the forge-proof merge gate consumes guarantees the author↔signing-
        // key consistency the gate checks (the operator's own messages pass), and — because the genesis party id
        // is seeded per-node-distinct (Program.cs) — two distinct-root nodes stamp DISTINCT party ids (no more
        // the install-constant "local" on every node). FAIL-CLOSED: if the seeded genesis's bound key does not
        // match this signer's key the host aborts rather than ship messages its own gate would drop.
        var activeMemberPartyId = ResolveAndAssertActiveMember();

        // SHIPPING: map over the conversation REGISTRY (full conversation addressing) — the bare route targets
        // the team channel, a conversation-addressed route resolves the named conversation. The DM resolve route
        // is mapped when the shipped DM feature flag is enabled (the default; sealed end-to-end via C4+C5). The
        // DM roster route is also mapped (GET /api/local-node/comms/dm/roster) so the Harborline App picker shows REAL
        // enrolled peers. The kill-switch (HARBORLINE_COMMS_DM_DISABLED) removes ALL DM routes in an incident.
        _sharedApp.MapApiRoutes(app =>
        {
            // ⚠ SECURITY (card #3383) — DESKTOP PLANE ONLY.
            //
            // `activeMemberPartyId` above is captured ONCE, here, at startup and stamped as the AUTHOR of
            // every appended message. `callerAuth.Validate` looks like the guard that would stop a web
            // caller and is not one: it returns Allow on the listener's gate-passed marker, which a
            // selected-session request carries — so it validates that the request passed the gate, never
            // who the caller is. Before this fence a signed-in MEMBER's message was authored as the
            // OPERATOR's roster party, signed with the node key, and merged into a CRDT that peers accept.
            //
            // That last part is why this is fenced rather than logged: the wrong author does not stay
            // local. It propagates to peers as a signed fact, and a CRDT merge has no notion of retracting
            // a provenance claim.
            //
            // The WHOLE family goes into the group — the reads too. A GET here returns the team's message
            // log, which is a read scope the member does not have under MTW-2, and with the writes refused
            // there is no remaining legitimate web-plane caller. Map new comms routes onto
            // `desktopPlaneOnly`, never onto `app`.
            //
            // Refusal is a RESTRICTION, not a fix: comms is desktop-only until a member can be authored as
            // themselves (#3379, the attribution half on CommsRoutes) under a permission model that resolves
            // real member authority (MTW-3). This makes the surface more closed and adds no allow path.
            var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();

            CommsRoutes.Map(
                desktopPlaneOnly, _conversations, _signer.Signer, _activeTeam, _callerAuth, activeMemberPartyId,
                _dmFlag, _roster, _timeProvider);
        });

        _logger.LogInformation(
            "Node-local comms API registered over the recoverable local-node store " +
            "(POST/GET {RouteBase} + {ConversationRoute}); inc-4 caller-auth enforced={CallerAuthEnforced}; " +
            "author = active enrolled member (roster-bound, not the install-constant \"local\"); " +
            "DM route enabled={DmEnabled} (sealed end-to-end via C4+C5 roster-bound keys; default ON, " +
            "kill-switch {DmKillSwitchVar}); DM roster route={DmRosterRoute} (live enrolled peers for picker).",
            CommsRoutes.RouteBase, CommsRoutes.ConversationRoute, _callerAuth.IsEnforced, _dmFlag.IsEnabled,
            CommsDmFeatureFlag.DisableEnvVarName, CommsRoutes.DmRosterRoute);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the active enrolled member's party id from the seeded trust roster (the genesis founder on a
    /// single-office node) and asserts the roster binds THIS signer's key to that party — the author↔signing-key
    /// consistency the production forge-proof merge gate requires. Throws (fail-closed) on a misconfiguration
    /// that would make the operator's own messages fail their own gate.
    /// </summary>
    private string ResolveAndAssertActiveMember()
    {
        var roster = _roster.Current;
        var partyId = roster.GenesisPartyId;

        // CONSISTENCY (the forge-proof invariant): the author party MUST be bound — in this very roster — to the
        // key this route signs with. If they diverge, the operator's own messages would be dropped by the merge
        // gate (party→bound-key mismatch). That is a composition error, not a runtime condition — fail the host.
        var boundKey = roster.PublicKeyOf(partyId);
        var signerKey = _signer.Signer.IssuerId;
        if (boundKey is null || !boundKey.Value.Equals(signerKey))
        {
            throw new InvalidOperationException(
                $"Comms author binding is inconsistent: the seeded trust roster does not bind the active member " +
                $"'{partyId}' to the comms signing key. The author-party-id and the signing key must be the same " +
                "roster entry (gap #2) or the production forge-proof merge gate would drop the operator's own " +
                "messages. Check the genesis self-admission in the composition root.");
        }
        return partyId;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
