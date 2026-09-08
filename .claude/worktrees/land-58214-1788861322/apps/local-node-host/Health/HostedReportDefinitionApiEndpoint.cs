using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the read-only report definition routes (ticket 085) onto the shared
/// Kestrel listener.
/// </summary>
/// <remarks>
/// Desktop-plane-only on ADR 0160 D5's rationale: these are pack-derived admin/config reads for
/// the desktop admin surface, and the confused-deputy fence that guards the Forms definition
/// family applies identically — a hosted-web principal must never read another tenant-context's
/// definition inventory through the node's ambient tenant. This family is deliberately NOT
/// device-reachable product data, unlike its route-base sibling <see cref="ReportsRoutes"/>
/// (report RUNS), so the fence expectation is keyed on the definitions base, not the parent.
/// Registered in <see cref="LocalNodeEndpointMapping"/> so its paths are mapped before Kestrel
/// starts; dependencies are singletons closed over by the route file (bug-2849-safe).
/// </remarks>
public sealed class HostedReportDefinitionApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IReportDefinitionRegistry _registry;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedReportDefinitionApiEndpoint> _logger;

    /// <summary>Constructs the hosted report-definition API endpoint.</summary>
    public HostedReportDefinitionApiEndpoint(
        SharedHostedWebApp sharedApp,
        IReportDefinitionRegistry registry,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedReportDefinitionApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
        {
            // Map new routes for this family onto `desktopPlaneOnly`, never onto `app`. Mapping
            // onto `app` compiles, serves, and silently reopens the confused deputy; the fence is
            // a property of the group, not of the route.
            var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();
            ReportDefinitionRoutes.Map(desktopPlaneOnly, _registry, _activeTeam);
        });

        _logger.LogInformation(
            "Desktop-plane-only report definition read API registered " +
            "(GET {RouteBase}; GET {RouteBase}/{{key}}; GET {RouteBase}/{{key}}/versions; " +
            "GET {RouteBase}/{{key}}/versions/{{version}}).",
            ReportDefinitionRoutes.RouteBase,
            ReportDefinitionRoutes.RouteBase,
            ReportDefinitionRoutes.RouteBase,
            ReportDefinitionRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
