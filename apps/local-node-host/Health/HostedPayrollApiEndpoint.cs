using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Payroll;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local payroll routes onto the shared Kestrel listener
/// (T4 local-first sweep — the payroll node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="PayrollRoutes.Map"/> (the single source of truth shared with any route tests so the wire
/// contract has no test/prod drift). The three payroll repos + the pay-run posting service are all
/// node-resident (recoverable <c>local-node.db</c>); they are resolved from the composition root and
/// passed to <see cref="PayrollRoutes.Map"/> as a service tuple.
/// </para>
/// <para>
/// Mapping order is explicit in <see cref="LocalNodeEndpointMapping"/>.
/// </para>
/// </remarks>
public sealed class HostedPayrollApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IServiceProvider _services;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedPayrollApiEndpoint> _logger;

    /// <summary>Constructs the hosted payroll API endpoint.</summary>
    public HostedPayrollApiEndpoint(
        SharedHostedWebApp sharedApp,
        IServiceProvider services,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedPayrollApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _services = services;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var payroll = (
            _services.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IEmployeeRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IPayRunRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Payroll.Services.IPayRunPostingService>());
        _sharedApp.MapApiRoutes(app => PayrollRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            payroll,
            _activeTeam,
            _timeProvider));

        _logger.LogInformation(
            "Node-local payroll API registered over the recoverable local-node store " +
            "(employees list/create + pay-runs list/get/create/post/reverse; pay-run post/reverse " +
            "post balanced JEs through the node GL-of-record under {RouteBase}).",
            PayrollRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
