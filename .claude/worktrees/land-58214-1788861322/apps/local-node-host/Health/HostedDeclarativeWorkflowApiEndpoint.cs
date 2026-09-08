using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local DECLARATIVE-interpreter human-task surfaces onto the shared Kestrel
/// listener (ADR 0135 A1 / 0143 R1-F): the generic <b>Effect Confirmations</b> surface
/// (<see cref="DeclarativeConfirmRoutes"/>) + the read-only executed-run <b>report</b>
/// (<see cref="WorkflowRunReportRoutes"/>). Companion to <see cref="HostedApprovalTaskApiEndpoint"/> (the typed
/// invoice/kg verticals).
/// </summary>
/// <remarks>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in the two static route classes (the
/// single source of truth shared with the route tests, so the wire contract has no test/prod drift). The read
/// models + the trigger dispatcher are node-resident and injected from the OUTER host container, then passed to
/// <c>Map</c> as closed-over dependencies — resolving via <c>[FromServices]</c> inside the handlers would fail
/// on the shared app's inner provider (bug-2849). Added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps paths while the shared app is still
/// pre-<c>StartAsync</c>.
/// </remarks>
public sealed class HostedDeclarativeWorkflowApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly NodeWorkflowConfirmationReadModel _confirmations;
    private readonly NodeWorkflowRunReportReadModel _report;
    private readonly IWorkflowTriggerDispatcher _dispatcher;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly AuthorizationGate _gate;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedDeclarativeWorkflowApiEndpoint> _logger;

    /// <summary>Constructs the hosted declarative-workflow API endpoint.</summary>
    public HostedDeclarativeWorkflowApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeWorkflowConfirmationReadModel confirmations,
        NodeWorkflowRunReportReadModel report,
        IWorkflowTriggerDispatcher dispatcher,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider timeProvider,
        ILogger<HostedDeclarativeWorkflowApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(confirmations);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _confirmations = confirmations;
        _report = report;
        _dispatcher = dispatcher;
        _activeTeam = activeTeam;
        _gate = gate;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
        {
            var deviceReachable = app.MapDeviceReachableProductDataGroup();
            DeclarativeConfirmRoutes.Map(
                deviceReachable,
                _confirmations,
                _dispatcher,
                _activeTeam,
                _gate,
                _timeProvider);
            WorkflowRunReportRoutes.Map(deviceReachable, _report, _activeTeam);
        });

        _logger.LogInformation(
            "Node-local declarative-interpreter API registered over the recoverable local-node store: effect " +
            "confirmations ({ConfirmBase}/*) — GET list, GET {{instanceId}}, POST {{instanceId}}/action — + the " +
            "executed-run report ({ReportBase}).",
            DeclarativeConfirmRoutes.RouteBase,
            WorkflowRunReportRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
