using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local journal-entry routes onto the shared Kestrel
/// listener (Cohort D — the financial-ledger node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="JournalEntryRoutes.Map"/> (the single source of truth shared with the route tests
/// so the wire contract has no test/prod drift). Reads compose the host-agnostic
/// <see cref="IJournalEntryQueryReadModel"/> over the node store; writes/reversals go through the
/// concrete node <see cref="NodeEfJournalStore"/>.
/// </para>
/// <para>
/// The read model and concrete store are resolved from the composition root and passed to
/// <see cref="JournalEntryRoutes.Map"/> as closed-over dependencies.
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedJournalEntryApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IJournalEntryQueryReadModel _readModel;
    private readonly NodeEfJournalStore _store;
    private readonly IJournalPostingService _posting;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedJournalEntryApiEndpoint> _logger;

    /// <summary>Constructs the hosted journal-entry API endpoint.</summary>
    public HostedJournalEntryApiEndpoint(
        SharedHostedWebApp sharedApp,
        IJournalEntryQueryReadModel readModel,
        NodeEfJournalStore store,
        IJournalPostingService posting,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedJournalEntryApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _readModel = readModel;
        _store = store;
        _posting = posting;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => JournalEntryRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _readModel,
            _store,
            _posting,
            _activeTeam,
            _timeProvider));

        _logger.LogInformation(
            "Node-local journal-entries API registered over the node-resident kernel-ledger " +
            "(GET/POST {RouteBase}, GET/POST {RouteBase}/{{id}}[/reverse]).",
            JournalEntryRoutes.RouteBase,
            JournalEntryRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
