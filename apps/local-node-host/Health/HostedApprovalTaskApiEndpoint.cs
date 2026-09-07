using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Workflow;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local CP human-task (approval-task / Ask-bar Inbox) routes onto the
/// shared Kestrel listener (ADR 0135 §2.8.2 — the invoice-approval vertical flow).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="InvoiceApprovalTaskRoutes.Map"/> (the single source of truth shared with the route tests so the
/// wire contract has no test/prod drift). The parked-task read model + the resume cutover are node-resident
/// (recoverable <c>local-node.db</c>) and injected from the OUTER host container, then passed to
/// <see cref="InvoiceApprovalTaskRoutes.Map"/> as closed-over dependencies. Resolving via
/// <c>[FromServices]</c> inside the route handlers would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider does NOT have the
/// outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedApprovalTaskApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeParkedTaskQueryReadModel _tasks;
    private readonly NodeInvoiceApprovalCutover _cutover;
    private readonly NodeKgActionApprovalCutover _kgActionCutover;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedApprovalTaskApiEndpoint> _logger;

    /// <summary>Constructs the hosted approval-task API endpoint.</summary>
    public HostedApprovalTaskApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeParkedTaskQueryReadModel tasks,
        NodeInvoiceApprovalCutover cutover,
        NodeKgActionApprovalCutover kgActionCutover,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedApprovalTaskApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(cutover);
        ArgumentNullException.ThrowIfNull(kgActionCutover);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _tasks = tasks;
        _cutover = cutover;
        _kgActionCutover = kgActionCutover;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
        {
            var deviceReachable = app.MapDeviceReachableProductDataGroup();
            InvoiceApprovalTaskRoutes.Map(deviceReachable, _tasks, _cutover, _activeTeam, _timeProvider);
            // KG-search Slice 2-actions — the parked proposed-CP-action human-task surface (G-G4:
            // basis-before-confirm; the ONLY path to execute is the approve on a real parked task).
            KgActionApprovalTaskRoutes.Map(
                deviceReachable,
                _tasks,
                _kgActionCutover,
                _activeTeam);
        });

        _logger.LogInformation(
            "Node-local CP human-task API registered over the recoverable local-node store: invoice-approval " +
            "({InvoiceBase}/*) + kg-action-approval ({KgBase}/*) — GET list, GET {{instanceId}}, " +
            "POST {{instanceId}}/action.",
            InvoiceApprovalTaskRoutes.RouteBase,
            KgActionApprovalTaskRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
