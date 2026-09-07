using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Blocks.Leases.Services;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local lease sub-ledger routes (ADR 0122 §D4 P2 → (b) — the offline
/// lease payment-history read path) onto the shared Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="LeaseSubLedgerRoutes.Map"/> (the single source of truth shared with the route tests so the
/// wire contract has no test/prod drift). The lease→sub-ledger READ chain composes the node-resident
/// <see cref="ILeaseSubLedgerLinkRepository"/> + <see cref="ISubLedgerReadModel"/> (wired by
/// <c>AddNodeLeaseSubLedgerReads</c>); the activate route uses the PM-pack
/// <see cref="LeaseSubLedgerService"/>.
/// </para>
/// <para>
/// The repositories + read-model + activation service are injected from the OUTER host container and
/// passed to <see cref="LeaseSubLedgerRoutes.Map"/> as closed-over dependencies. Resolving via
/// <c>[FromServices]</c> inside the route handlers would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider does NOT have
/// the outer-container registrations (bug-2849) — the same pattern as
/// <see cref="HostedJournalEntryApiEndpoint"/> / <see cref="HostedInvoiceApiEndpoint"/>.
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedLeaseSubLedgerApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILeaseSubLedgerLinkRepository _links;
    private readonly ISubLedgerReadModel _readModel;
    private readonly LeaseSubLedgerService _activation;
    private readonly TimeProvider _time;
    private readonly ILogger<HostedLeaseSubLedgerApiEndpoint> _logger;

    /// <summary>Constructs the hosted lease sub-ledger API endpoint.</summary>
    public HostedLeaseSubLedgerApiEndpoint(
        SharedHostedWebApp sharedApp,
        ILeaseSubLedgerLinkRepository links,
        ISubLedgerReadModel readModel,
        LeaseSubLedgerService activation,
        IActiveTeamAccessor activeTeam,
        TimeProvider time,
        ILogger<HostedLeaseSubLedgerApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _links = links;
        _readModel = readModel;
        _activation = activation;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => LeaseSubLedgerRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _links,
            _readModel,
            _activation,
            _activeTeam,
            _time));

        _logger.LogInformation(
            "ADR 0122 §D4 P2 -> (b) node-local lease sub-ledger API registered over the node-resident " +
            "ADR 0120 read-model (GET {RouteBase}/{{name}}/payment-history, " +
            "POST {RouteBase}/{{name}}/activate-subledger).",
            LeaseSubLedgerRoutes.RouteBase,
            LeaseSubLedgerRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
