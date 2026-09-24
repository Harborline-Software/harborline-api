using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the catalogue read family using outer-container dependencies.</summary>
public sealed class HostedCatalogueApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ICatalogue _catalogue;
    private readonly IPackInstallStore _packStore;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<HostedCatalogueApiEndpoint> _logger;
    private readonly CatalogueDetailRuntime? _detailRuntime;

    public HostedCatalogueApiEndpoint(
        SharedHostedWebApp sharedApp,
        ICatalogue catalogue,
        IPackInstallStore packStore,
        ITenantContext tenantContext,
        ILogger<HostedCatalogueApiEndpoint> logger,
        CatalogueDetailRuntime? detailRuntime = null)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _packStore = packStore ?? throw new ArgumentNullException(nameof(packStore));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _detailRuntime = detailRuntime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => CatalogueRoutes.Map(
            app.MapSelectedSessionProductGroup(), _catalogue, _packStore, _tenantContext));
        if (_detailRuntime is not null)
            _sharedApp.MapApiRoutes(app => CatalogueDetailRoutes.Map(app.MapSelectedSessionProductGroup(), _detailRuntime));
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
