using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local clipped KG keyword-search route onto the shared Kestrel listener
/// (Harborline App KG keyword-search demo, ONR survey
/// <c>onr-carrier-kg-search-calendar-demo-survey-2026-06-24</c>).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="KgSearchRoutes.Map"/> (the single source of truth shared with the route tests so the wire
/// contract has no test/prod drift). The clipped read service + the active-team accessor are resolved from
/// the OUTER host container (where the KG composition + the live <c>IGrantStore</c> are registered) and
/// passed to <see cref="KgSearchRoutes.Map"/> as closed-over dependencies. Resolving via
/// <c>[FromServices]</c> inside the route handler would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider does NOT have the
/// outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps the path while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedKgSearchApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly NodeSearchReadService _readService;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeTeamRoster _roster;
    private readonly NodePrincipalSigner _nodeSigner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedKgSearchApiEndpoint> _logger;

    /// <summary>Constructs the hosted clipped KG keyword-search API endpoint.</summary>
    public HostedKgSearchApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeSearchReadService readService,
        IActiveTeamAccessor activeTeam,
        NodeTeamRoster roster,
        NodePrincipalSigner nodeSigner,
        TimeProvider timeProvider,
        ILogger<HostedKgSearchApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(readService);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(nodeSigner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _readService = readService;
        _activeTeam = activeTeam;
        _roster = roster;
        _nodeSigner = nodeSigner;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            KgSearchRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                _readService,
                _activeTeam,
                ResolveCurrentPrincipal,
                _timeProvider));

        _logger.LogInformation(
            "Node-local clipped KG keyword-search API registered over the fail-closed read service " +
            "(GET {RouteBase}/search). Every result is clipped to the acting principal's authorized records.",
            KgSearchRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private ActorId ResolveCurrentPrincipal() => new(_roster.Current.Members
        .Single(member => member.PublicKey.Equals(_nodeSigner.Signer.IssuerId)).PartyId);
}
