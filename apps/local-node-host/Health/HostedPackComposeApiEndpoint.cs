using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Compose;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the Pack Composer B-2a COMPOSE ceremony routes
/// (<see cref="PackComposeRoutes.Map"/>) onto the shared Kestrel listener — the snapshot / affirm /
/// guarded-export surface the Composer UI drives. A thin <see cref="IHostedService"/> wrapper (the route
/// logic is the single source of truth shared with the route tests). The ceremony + exporter + node
/// principal signer + active-team accessor are resolved from the OUTER host container and passed as
/// closed-over dependencies (bug-2849: the routes map onto the shared inner <c>WebApplication</c>).
/// </summary>
/// <remarks>
/// <b>Desktop-plane only.</b> The ceremony culminates in a portable pack signed by the node identity.
/// Snapshot, affirmation, and export are one security transaction, so the full family requires the
/// listener's positive <see cref="DesktopPlaneRequestFeature"/> assertion; missing attribution cannot
/// inherit the operator's <c>packages:author</c> grant.
/// </remarks>
public sealed class HostedPackComposeApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ComposeCeremony _ceremony;
    private readonly IPackExporter _exporter;
    private readonly NodePrincipalSigner _signer;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly AuthorizationGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<HostedPackComposeApiEndpoint> _logger;

    /// <summary>Constructs the hosted compose-ceremony API endpoint.</summary>
    public HostedPackComposeApiEndpoint(
        SharedHostedWebApp sharedApp,
        ComposeCeremony ceremony,
        IPackExporter exporter,
        NodePrincipalSigner signer,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider timeProvider,
        ILogger<HostedPackComposeApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _ceremony = ceremony ?? throw new ArgumentNullException(nameof(ceremony));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
        {
            var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();
            PackComposeRoutes.Map(
                desktopPlaneOnly,
                _ceremony,
                _exporter,
                _signer.Signer,
                _activeTeam,
                _gate,
                _time,
                _logger);
        });

        _logger.LogInformation(
            "Pack Composer B-2a compose ceremony API registered (POST/GET {ComposeRoute}[/{{id}}/affirm|export]).",
            PackComposeRoutes.ComposeRoute);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
