using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Diagnostics;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the RUNTIME ADMISSION routes (gap #3) onto the shared Kestrel listener — the backend
/// the Harborline App QR-scan / invite-entry UI (a flagged FED follow-on) drives. The admission analogue of
/// <see cref="HostedCommsApiEndpoint"/>: a thin <see cref="IHostedService"/> wrapper whose route mapping lives in
/// <see cref="AdmissionRoutes.Map"/> (the single source of truth).
/// </summary>
/// <remarks>
/// <para>
/// The coordinator, the install-level <see cref="NodeTeamRoster"/>, the admitter's signer
/// (<see cref="NodePrincipalSigner"/>), the verifier, and the roster-sync projection are injected from the OUTER
/// host container and passed to <see cref="AdmissionRoutes.Map"/> as closed-over dependencies — resolving via
/// <c>[FromServices]</c> inside the handlers would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose provider lacks the outer registrations
/// (bug-2849).
/// </para>
/// <para>
/// <b>Caller-auth.</b> The admission routes are NOT in the <c>SharedHostedWebApp.CallerAuthAllowlist</c>, so the
/// listener-level inc-4 F1 middleware gates them by default (401 for a tokenless caller when a session token is
/// configured). Admission is an admin-only operation; there is no per-route exemption.
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
internal sealed class HostedAdmissionApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly AdmissionCoordinator _coordinator;
    private readonly NodeTeamRoster _roster;
    private readonly NodePrincipalSigner _signer;
    private readonly IOperationVerifier _verifier;
    private readonly RosterCrdtProjection _projection;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly IEnrollmentCompensatingControlRecorder _sodAudit;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IWebSelectedSessionIdentityAuthority? _selectedIdentity;
    private readonly NodeEnrollmentJoinService? _joinService;
    // #3167 — the web-admitted PAIRING dispatch bundle. When present, the redeem route is mode-exclusive (R2):
    // a bound pairing token routes to the token-gated pairing admitter; a binding-absent redeem refuses opaque
    // when web-plane admission is enabled. Null on a plain-only host (the redeem route stays open-enrollment).
    private readonly PairingRedeemDispatch? _pairing;
    private readonly ILogger<HostedAdmissionApiEndpoint> _logger;
    private readonly CommsDiagnostics _diag;

    /// <summary>Constructs the hosted admission API endpoint.</summary>
    /// <remarks>
    /// <paramref name="joinService"/> is OPTIONAL: it is registered only when this node is configured to JOIN a
    /// peer team (the joiner client + transport are wired). When absent (a node that only HOSTS/admits) the
    /// <c>POST /admission/join</c> trigger route is simply not mapped (gap #1 cerebrum [2026-06-21]).
    /// <paramref name="diag"/> is the ADDITIVE dev-diagnostics seam (default-on pre-release; null ⇒ disabled no-op).
    /// </remarks>
    public HostedAdmissionApiEndpoint(
        SharedHostedWebApp sharedApp,
        AdmissionCoordinator coordinator,
        NodeTeamRoster roster,
        NodePrincipalSigner signer,
        IOperationVerifier verifier,
        RosterCrdtProjection projection,
        NodeCallerSessionToken callerAuth,
        IEnrollmentCompensatingControlRecorder sodAudit,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedAdmissionApiEndpoint> logger,
        IWebSelectedSessionIdentityAuthority? selectedIdentity = null,
        NodeEnrollmentJoinService? joinService = null,
        CommsDiagnostics? diag = null,
        PairingRedeemDispatch? pairing = null)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(sodAudit);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _coordinator = coordinator;
        _roster = roster;
        _signer = signer;
        _verifier = verifier;
        _projection = projection;
        _callerAuth = callerAuth;
        _sodAudit = sodAudit;
        _activeTeam = activeTeam;
        _selectedIdentity = selectedIdentity;
        _joinService = joinService;
        _pairing = pairing;
        _logger = logger;
        _diag = diag ?? CommsDiagnostics.Disabled;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The local ADMITTER is the seeded genesis member (single-office node), and the route signs admissions
        // with this node's principal signer. Assert — fail-closed — that the seeded roster binds THIS signer's key
        // to that party, the same author↔signing-key consistency the comms gate requires: MemberRoster.Admit
        // re-checks the admitter signer's key against the admitter's roster binding, so a mismatch would make
        // every admit throw at runtime. Catching it here turns a composition error into a loud startup failure.
        var admitterPartyId = ResolveAndAssertAdmitter();

        _sharedApp.MapApiRoutes(app =>
        {
            // AdmissionRoutes.MapCore installs the admission-only founder-standing fence so it covers
            // BOTH branches below. Production takes the pairing branch, the one a harness is least likely
            // to build; keeping the fence at the funnel prevents either branch from bypassing it.

            if (_pairing is not null)
            {
                // #3167 — the FULL surface incl. the mode-exclusive pairing path (R2). The internal overload wires
                // the pairing dispatch into the redeem route; it reads the active team from the pairing bundle.
                AdmissionRoutes.Map(
                    app, _coordinator, _roster, _signer.Signer, admitterPartyId, _verifier, _projection,
                    _sodAudit, _pairing, _joinService, _diag, _selectedIdentity);
            }
            else
            {
                AdmissionRoutes.Map(
                    app, _coordinator, _roster, _signer.Signer, admitterPartyId, _verifier, _projection,
                    _sodAudit, _activeTeam, _joinService, _diag, _selectedIdentity);
            }
        });

        _logger.LogInformation(
            "Runtime admission API registered: GET {Base}/identity (node self-identity for joiner) + "
            + "POST {Base}/invites (generate-invite) + POST {Base}/redeem "
            + "(redeem-invite → signed admission published to roster-sync + transport-key wired + SoD audit "
            + "recorded — #1295 F1){JoinRoute}; admitter = seeded genesis member '{Admitter}'; inc-4 caller-auth "
            + "enforced={CallerAuthEnforced} (gated by default).",
            AdmissionRoutes.RouteBase, AdmissionRoutes.RouteBase, AdmissionRoutes.RouteBase,
            _joinService is not null
                ? $" + POST {AdmissionRoutes.RouteBase}/join (B-side join trigger → enroll + adopt + active-team-"
                    + "switch + daemon-rebind)"
                : " (no /join — node does not join peer teams)",
            admitterPartyId, _callerAuth.IsEnforced);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve the local admitter party id (the seeded genesis founder) and assert the seeded roster binds THIS
    /// signer's key to it. Throws (fail-closed) on a misconfiguration that would make every admit throw at
    /// runtime (the admitter-key-binding check inside <see cref="MemberRoster.Admit"/>).
    /// </summary>
    private string ResolveAndAssertAdmitter()
    {
        var current = _roster.Current;
        var partyId = current.GenesisPartyId;

        var boundKey = current.PublicKeyOf(partyId);
        var signerKey = _signer.Signer.IssuerId;
        if (boundKey is null || !boundKey.Value.Equals(signerKey))
        {
            throw new InvalidOperationException(
                $"Admission admitter binding is inconsistent: the seeded trust roster does not bind the local "
                + $"admitter '{partyId}' to the admission signing key. MemberRoster.Admit re-checks the admitter "
                + "signer's key against its roster binding, so every admit would throw. Check the genesis "
                + "self-admission in the composition root.");
        }
        return partyId;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
