using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Registers the portability export routes on the shared node listener.</summary>
public sealed class HostedDataExportApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IDataExportService _exports;
    private readonly ITenantContext _tenantContext;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly ILogger<HostedDataExportApiEndpoint> _logger;

    /// <summary>Creates the hosted portability export route registrar.</summary>
    public HostedDataExportApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDataExportService exports,
        ITenantContext tenantContext,
        NodeCallerSessionToken callerAuth,
        ILogger<HostedDataExportApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _exports = exports;
        _tenantContext = tenantContext;
        _callerAuth = callerAuth;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => DataExportRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            _exports,
            _tenantContext,
            _callerAuth));
        _logger.LogInformation(
            "Tenant portability export API registered at {RouteBase}; caller-auth enforced={CallerAuthEnforced}.",
            DataExportRoutes.RouteBase,
            _callerAuth.IsEnforced);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
