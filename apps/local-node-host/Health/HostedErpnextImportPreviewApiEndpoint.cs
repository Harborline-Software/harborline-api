using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Registers the read-only ERPNext source-inventory preview before the shared Kestrel app starts.
/// </summary>
public sealed class HostedErpnextImportPreviewApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ILogger<HostedErpnextImportPreviewApiEndpoint> _logger;

    /// <summary>Creates the hosted route mapper.</summary>
    public HostedErpnextImportPreviewApiEndpoint(
        SharedHostedWebApp sharedApp,
        ILogger<HostedErpnextImportPreviewApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => ErpnextImportPreviewRoutes.Map(
            app.MapSelectedSessionProductGroup()));
        _logger.LogInformation(
            "ERPNext source-inventory preview API registered at {PreviewRoute}.",
            ErpnextImportPreviewRoutes.PreviewRoute);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
