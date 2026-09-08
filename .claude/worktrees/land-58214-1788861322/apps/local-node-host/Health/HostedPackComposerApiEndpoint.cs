using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the Pack Composer B-1a EXPORT + VERIFY routes
/// (<see cref="PackComposerRoutes.Map"/>) onto the shared Kestrel listener. A thin
/// <see cref="IHostedService"/> wrapper — the route logic is the single source of truth shared with
/// the route tests. The exporter/verifier + the node principal signer + the active-team accessor are
/// resolved from the OUTER host container and passed as closed-over dependencies (bug-2849: the
/// routes map onto the shared inner <c>WebApplication</c>, whose provider lacks the outer
/// registrations).
/// </summary>
/// <remarks>
/// <para>
/// The v1 trust store recognizes the node's OWN roster key (the node principal signer's public key)
/// as an own-roster CURRENT root at <see cref="PackComposerRoutes.OwnRosterEpoch"/> — so an
/// own-authored pack round-trips export → verify → Verified. The Harborline-channel root + revocation
/// list attach with the B-1b install path.
/// </para>
/// <para>
/// <b>Desktop-plane only.</b> Export emits a portable pack signed by the node identity, so missing
/// attribution cannot inherit the operator's <c>packages:author</c> grant. Export and its sibling
/// verify route require the listener's positive <see cref="DesktopPlaneRequestFeature"/> assertion.
/// </para>
/// </remarks>
public sealed class HostedPackComposerApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IPackExporter _exporter;
    private readonly IPackVerifier _verifier;
    private readonly NodePrincipalSigner _signer;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly AuthorizationGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<HostedPackComposerApiEndpoint> _logger;

    /// <summary>Constructs the hosted pack-composer API endpoint.</summary>
    public HostedPackComposerApiEndpoint(
        SharedHostedWebApp sharedApp,
        IPackExporter exporter,
        IPackVerifier verifier,
        NodePrincipalSigner signer,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider timeProvider,
        ILogger<HostedPackComposerApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // v1 own-roster trust store — the node's own signing key, current epoch.
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(
                TrustScope.OwnRoster,
                _signer.Signer.IssuerId,
                PackComposerRoutes.OwnRosterEpoch,
                TrustRootStatus.Current),
        });

        _sharedApp.MapApiRoutes(app =>
        {
            var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();
            PackComposerRoutes.Map(
                desktopPlaneOnly,
                _exporter,
                _verifier,
                trustStore,
                _signer.Signer,
                _activeTeam,
                _gate,
                _time,
                _logger);
        });

        _logger.LogInformation(
            "Pack Composer B-1a export/verify API registered (POST {ExportRoute}, POST {VerifyRoute}).",
            PackComposerRoutes.ExportRoute, PackComposerRoutes.VerifyRoute);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
