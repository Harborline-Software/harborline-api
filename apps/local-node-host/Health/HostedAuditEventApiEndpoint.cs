using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Audit;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local audit-events routes onto the shared Kestrel listener
/// (T4 audit system-of-record node-flip; ADR 0126).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="AuditEventRoutes.Map"/> (the single source of truth shared with the route tests so the
/// wire contract has no test/prod drift). Reads go through the node <see cref="NodeAuditEventReader"/>
/// over the recoverable <c>node_audit_events</c> table in <c>local-node.db</c>.
/// </para>
/// <para>
/// The reader is injected from the OUTER host container and passed to <see cref="AuditEventRoutes.Map"/>
/// as a closed-over dependency. Resolving via <c>[FromServices]</c> inside the route handlers would
/// fail because the routes are mapped onto <see cref="SharedHostedWebApp"/>'s inner
/// <c>WebApplication</c>, whose service provider does NOT have the outer-container registrations
/// (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedAuditEventApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeAuditEventReader _reader;
    private readonly ILogger<HostedAuditEventApiEndpoint> _logger;

    /// <summary>Constructs the hosted audit-event API endpoint.</summary>
    public HostedAuditEventApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeAuditEventReader reader,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedAuditEventApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _reader = reader;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => AuditEventRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _reader,
            _activeTeam));

        _logger.LogInformation(
            "Node-local audit-events API registered over the recoverable local-node.db audit store " +
            "(GET {RouteBase}, GET {RouteBase}/{{id}}).",
            AuditEventRoutes.RouteBase,
            AuditEventRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
