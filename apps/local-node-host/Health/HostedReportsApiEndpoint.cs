using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Reports;
using Harborline.Api.Blocks.Reports.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T5 local-first sweep hosted service that drains the report cartridge registry and maps the
/// node-local report-run + chart-list routes (the read-side report family) on the shared Kestrel
/// listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="ReportsRoutes.Map"/> (the single source of truth shared with the route tests). The
/// routes are backed by the reports cartridge substrate (<see cref="IReportRunner"/> over the node
/// <c>IGeneralLedgerReadModel</c> / <c>IJournalStore</c> / <c>IAccountResolver</c> /
/// <c>IArAgingService</c> / <c>IApAgingService</c> / <c>IChartRepository</c>), a read-only
/// projection over <see cref="LocalNodeDbContext"/> (the now-node-resident GL).
/// </para>
/// <para>
/// <b>Registry drain.</b> The cartridge substrate registers each cartridge's
/// <c>ICartridgeRegistrar</c> at composition time; the runner resolves cartridges from the
/// <c>ReportCartridgeRegistry</c> at run-time, so the registry MUST be drained once at startup
/// (after the provider is built, before the first run). <see cref="ReportSubstrateServiceProviderExtensions.UseBlocksReports"/>
/// is called here in <see cref="StartAsync"/> — the same pattern the Bridge uses post-build, but
/// the node has no <c>WebApplication</c> at composition time, so a hosted-service StartAsync is the
/// natural drain point. The drain is guarded so a re-start cannot double-drain (the registry rejects
/// duplicate registrations).
/// </para>
/// <para>
/// The runner + context factory are injected from the OUTER host container and passed to
/// <see cref="ReportsRoutes.Map"/> as closed-over dependencies (bug-2849 — NOT
/// <c>[FromServices]</c> on the inner shared-app container). The root <see cref="IServiceProvider"/>
/// is the outer provider used to drain the registrars. Registered before
/// <see cref="SharedHostedWebApp"/> so paths are mapped before Kestrel starts.
/// </para>
/// </remarks>
public sealed class HostedReportsApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IReportRunner _runner;
    private readonly IDbContextFactory<LocalNodeDbContext> _factory;
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<HostedReportsApiEndpoint> _logger;

    private bool _drained;

    /// <summary>Constructs the hosted reports API endpoint.</summary>
    public HostedReportsApiEndpoint(
        SharedHostedWebApp sharedApp,
        IReportRunner runner,
        IDbContextFactory<LocalNodeDbContext> factory,
        IServiceProvider rootProvider,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedReportsApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _runner = runner;
        _factory = factory;
        _rootProvider = rootProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Drain every ICartridgeRegistrar into the ReportCartridgeRegistry once, before the first
        // run resolves a cartridge. UseBlocksReports throws on a second drain (the registry rejects
        // duplicate registrations), so guard against a re-start.
        if (!_drained)
        {
            var drained = _rootProvider.UseBlocksReports();
            _drained = true;
            _logger.LogInformation(
                "T5 node-local reports: drained {Count} report cartridge registrar(s) into the registry.",
                drained);
        }

        _sharedApp.MapApiRoutes(app => ReportsRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _runner,
            _factory,
            _activeTeam));

        _logger.LogInformation(
            "T5 node-local reports API registered (POST {ReportsBase}/{{kind}} + GET {ChartsRoute}).",
            ReportsRoutes.ReportsRouteBase, ReportsRoutes.ChartsRoute);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
