using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Layout;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the Layout denial reader before the shared listener starts.</summary>
public sealed class HostedLayoutDenialApiEndpoint(
    SharedHostedWebApp sharedApp,
    LayoutDenialReader reader,
    IActiveTeamAccessor activeTeam,
    TimeProvider time,
    ILogger<HostedLayoutDenialApiEndpoint> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sharedApp.MapApiRoutes(app => LayoutDenialRoutes.Map(
            app.MapDeviceReachableProductDataGroup(), reader, activeTeam, time));
        logger.LogInformation("Layout denial reader API registered at GET {RouteBase}.", LayoutDenialRoutes.RouteBase);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
