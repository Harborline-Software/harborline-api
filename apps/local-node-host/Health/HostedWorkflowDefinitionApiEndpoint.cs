using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local workflow-DEFINITION authoring routes (WF-KEY W-7 / G5)
/// onto the shared Kestrel listener. The process analog of <see cref="HostedFormsApiEndpoint"/>: a
/// thin <see cref="IHostedService"/> wrapper whose route mapping lives in
/// <see cref="WorkflowDefinitionRoutes.Map"/> (the single source of truth shared with the route
/// tests, so the wire contract has no test/prod drift).
/// </summary>
/// <remarks>
/// The store + active-team accessor are resolved from the OUTER host container and passed to
/// <see cref="WorkflowDefinitionRoutes.Map"/> as closed-over dependencies (resolving via
/// <c>[FromServices]</c> inside the handlers would fail because the routes map onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose provider is a separate
/// container — bug-2849). Registered BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </remarks>
public sealed class HostedWorkflowDefinitionApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly AuthorizedWorkflowDefinitionLifecycle _store;
    private readonly TimeProvider _timeProvider;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedWorkflowDefinitionApiEndpoint> _logger;

    /// <summary>Constructs the hosted workflow-definition authoring API endpoint.</summary>
    public HostedWorkflowDefinitionApiEndpoint(
        SharedHostedWebApp sharedApp,
        AuthorizedWorkflowDefinitionLifecycle store,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedWorkflowDefinitionApiEndpoint> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _store = store;
        _activeTeam = activeTeam;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => WorkflowDefinitionRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _store,
            _activeTeam,
            _timeProvider));

        _logger.LogInformation(
            "WF-KEY node-local workflow-definition authoring API registered — GET/PUT {Base}[/{{key}}]. " +
            "First-party authored + admitted definitions; STORAGE only (fail-closed admission at persist; " +
            "execution stays gated on the ADR-0143 broker-PEP seam).",
            WorkflowDefinitionRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
