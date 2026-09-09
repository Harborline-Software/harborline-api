using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0134 P1b-2 / P2 hosted service that maps the canonical-node-key principal
/// SIGNING route (<see cref="CurrentPrincipalSignatureRoutes.Route"/>) on the shared
/// Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="CurrentPrincipalSignatureRoutes.Map"/> (the single source of truth shared
/// with the route tests, so the wire contract has no test/prod drift). The route is
/// backed by the node's canonical <see cref="NodePrincipalSigner"/> — the
/// <c>RootSeedHex</c>→Ed25519 identity — injected here from the OUTER host container and
/// passed to <see cref="CurrentPrincipalSignatureRoutes.Map"/> as closed-over deps.
/// </para>
/// <para>
/// Mapping order is explicit in <see cref="LocalNodeEndpointMapping"/> and completes before the
/// executable endpoint registry is sealed.
/// </para>
/// <para>
/// <b>Desktop-plane only.</b> This endpoint is a node-key attestation surface. It must
/// receive the listener's positive <see cref="DesktopPlaneRequestFeature"/> assertion;
/// missing, selected-session, and device attribution refuse before the node signs.
/// </para>
/// </remarks>
public sealed class HostedCurrentPrincipalSignatureApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly NodePrincipalSigner _nodeSigner;
    private readonly NodeTeamRoster _roster;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedCurrentPrincipalSignatureApiEndpoint> _logger;

    /// <summary>Constructs the hosted current-principal-signature API endpoint.</summary>
    public HostedCurrentPrincipalSignatureApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodePrincipalSigner nodeSigner,
        NodeTeamRoster roster,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider,
        ILogger<HostedCurrentPrincipalSignatureApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(nodeSigner);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _nodeSigner = nodeSigner;
        _roster = roster;
        _callerAuth = callerAuth;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
        {
            var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();
            CurrentPrincipalSignatureRoutes.Map(
                desktopPlaneOnly,
                _nodeSigner.Signer,
                _nodeSigner.NodePublicKey,
                _callerAuth,
                ResolveCurrentPrincipal,
                _timeProvider);
        });

        // SECURITY: log the node PUBLIC key only — it is public trust-anchor material, not
        // secret. The private key is never logged anywhere.
        _logger.LogInformation(
            "ADR 0134 P1b-2/P2 canonical-node-key principal-signing route registered on shared " +
            "hosted web-app (GET {Route}); node public key {NodePublicKey}. " +
            "INC-4 caller-auth: session-token enforced={CallerAuthEnforced} " +
            "(false = dev/single-host-trusted, no token injected).",
            CurrentPrincipalSignatureRoutes.Route, _nodeSigner.NodePublicKey, _callerAuth.IsEnforced);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private ActorId ResolveCurrentPrincipal() => new(_roster.Current.Members
        .Single(member => member.PublicKey.Equals(_nodeSigner.Signer.IssuerId)).PartyId);
}
