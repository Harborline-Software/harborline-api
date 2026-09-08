using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.OrgBranding;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local org-branding routes (resolve / logo / write / upload) onto the
/// shared Kestrel listener (tenant-branding slice T1).
/// </summary>
/// <remarks>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="OrgBrandingRoutes.Map"/> (the single source of truth shared with the route tests so the wire
/// contract has no test/prod drift). The store / resolver / blob store / active-team accessor are resolved
/// from the OUTER host container and passed to <see cref="OrgBrandingRoutes.Map"/> as closed-over
/// dependencies — resolving via <c>[FromServices]</c> inside the handlers would fail because the routes are
/// mapped onto <see cref="SharedHostedWebApp"/>'s inner provider (bug-2849).
/// </remarks>
public sealed class HostedOrgBrandingApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IOrgBrandingStore _store;
    private readonly IOrgBrandingResolver _resolver;
    private readonly IBlobStore _blobs;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedOrgBrandingApiEndpoint> _logger;

    /// <summary>Constructs the hosted org-branding API endpoint.</summary>
    public HostedOrgBrandingApiEndpoint(
        SharedHostedWebApp sharedApp,
        IOrgBrandingStore store,
        IOrgBrandingResolver resolver,
        IBlobStore blobs,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedOrgBrandingApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _store = store;
        _resolver = resolver;
        _blobs = blobs;
        _activeTeam = activeTeam;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            OrgBrandingRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                _store,
                _resolver,
                _blobs,
                _activeTeam,
                _timeProvider));

        _logger.LogInformation(
            "Node-local org-branding API registered over the recoverable local-node store " +
            "(GET/PUT {RouteBase}; GET/POST {RouteBase}/logo).",
            OrgBrandingRoutes.RouteBase,
            OrgBrandingRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
