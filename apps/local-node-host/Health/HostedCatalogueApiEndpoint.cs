using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the catalogue read family using outer-container dependencies.</summary>
public sealed class HostedCatalogueApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ICatalogue _catalogue;
    private readonly ILogger<HostedCatalogueApiEndpoint> _logger;

    public HostedCatalogueApiEndpoint(
        SharedHostedWebApp sharedApp,
        ICatalogue catalogue,
        ILogger<HostedCatalogueApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => CatalogueRoutes.Map(
            app.MapSelectedSessionProductGroup(), _catalogue));
        // CA1873: the arguments are evaluated before the level is consulted, so the guard is the
        // remediation the rule asks for even on a once-per-start registration message.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Catalogue definition read API registered (GET {RouteBase}; GET {TypesRoute}).",
                CatalogueRoutes.RouteBase, CatalogueRoutes.TypesRoute);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
