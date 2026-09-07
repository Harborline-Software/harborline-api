using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAp.Services;

using Harborline.Api.LocalNodeHost.Data.Financial;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local AP bill routes onto the shared Kestrel listener
/// (Cohort D Step 2c — the AP node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="BillRoutes.Map"/> (the single source of truth shared with the route tests so the wire
/// contract has no test/prod drift). Reads come from the <see cref="NodeEfBillRepository"/>;
/// writes (create+record / void / approve / dispute / resolve-dispute) go through the
/// <see cref="IBillPostingService"/> — whose <c>RecordAsync</c> posts the auto-JE via the
/// Step-2a node posting service over the recoverable <see cref="NodeEfJournalStore"/>.
/// </para>
/// <para>
/// The accessors are injected from the OUTER host container and passed to
/// <see cref="BillRoutes.Map"/> as closed-over dependencies. Resolving via <c>[FromServices]</c>
/// inside the route handlers would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider does NOT
/// have the outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedBillApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeEfBillRepository _bills;
    private readonly IBillPostingService _posting;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedBillApiEndpoint> _logger;

    /// <summary>Constructs the hosted bill API endpoint.</summary>
    public HostedBillApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeEfBillRepository bills,
        IBillPostingService posting,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedBillApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _bills = bills;
        _posting = posting;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => BillRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _bills,
            _posting,
            _activeTeam,
            _timeProvider));

        _logger.LogInformation(
            "Node-local AP bills API registered over the recoverable local-node store " +
            "(GET/POST {RouteBase}, GET {RouteBase}/{{id}}, POST {RouteBase}/{{id}}/[void|approve|dispute|resolve-dispute]).",
            BillRoutes.RouteBase,
            BillRoutes.RouteBase,
            BillRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
