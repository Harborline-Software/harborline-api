using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the catalogue read family using outer-container dependencies.</summary>
public sealed class HostedCatalogueApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ICatalogue _catalogue;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedCatalogueApiEndpoint> _logger;

    public HostedCatalogueApiEndpoint(
        SharedHostedWebApp sharedApp,
        ICatalogue catalogue,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedCatalogueApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => CatalogueRoutes.Map(
            app.MapDesktopPlaneOnlyGroup(), _catalogue, _activeTeam));
        _logger.LogInformation("Catalogue definition read API registered (GET {RouteBase}; GET {TypesRoute}).",
            CatalogueRoutes.RouteBase, CatalogueRoutes.TypesRoute);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
