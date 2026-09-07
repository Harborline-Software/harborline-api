using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the read-only node-local spatial-frame resolution routes onto the
/// shared Kestrel listener (ADR 0168 D2; card G4.1). Thin <see cref="IHostedService"/> wrapper
/// around <see cref="SpatialFrameRoutes.Map"/> (the single source of truth shared with the route
/// tests); the store + active-team accessor are injected from the OUTER host container and closed
/// over — never resolved via <c>[FromServices]</c> inside the handlers (bug-2849).
/// </summary>
public sealed class HostedSpatialFrameApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ISpatialFrameDescriptorStore _frames;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedSpatialFrameApiEndpoint> _logger;

    /// <summary>Constructs the hosted spatial-frame read API endpoint.</summary>
    public HostedSpatialFrameApiEndpoint(
        SharedHostedWebApp sharedApp,
        ISpatialFrameDescriptorStore frames,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedSpatialFrameApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => SpatialFrameRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            _frames,
            _activeTeam));

        _logger.LogInformation(
            "Node-local spatial-frame read API registered (GET {Base}/{{anchor}}/{{frameCode}}, " +
            "GET {Base}/{{anchor}}/{{frameCode}}/{{frameEpoch}}). Read-only; tenant-scoped; " +
            "attestation and quarantine not exposed.",
            SpatialFrameRoutes.RouteBase, SpatialFrameRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
