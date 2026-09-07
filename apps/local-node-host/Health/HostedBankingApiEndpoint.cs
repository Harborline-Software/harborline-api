using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local banking routes onto the shared Kestrel listener
/// (T3 local-first sweep — the banking node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="BankAccountRoutes.Map"/> (the single source of truth shared with any route tests so the
/// wire contract has no test/prod drift). The four banking repos + the import pipeline + matching /
/// accept / un-match services + the fiscal-period repo + the mock bank feed are all node-resident
/// (recoverable <c>local-node.db</c>); they are resolved from the composition root and passed to
/// <see cref="BankAccountRoutes.Map"/> as a service tuple.
/// </para>
/// <para>
/// Mapping order is explicit in <see cref="LocalNodeEndpointMapping"/>.
/// </para>
/// </remarks>
public sealed class HostedBankingApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IServiceProvider _services;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedBankingApiEndpoint> _logger;

    /// <summary>Constructs the hosted banking API endpoint.</summary>
    public HostedBankingApiEndpoint(
        SharedHostedWebApp sharedApp,
        IServiceProvider services,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedBankingApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var banking = (
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IBankAccountRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IStatementLineRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IMatchLinkRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Services.IReconciliationRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Import.ImportPipelineService>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Matching.AcceptMatchService>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Matching.UnMatchService>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Matching.ReconciliationLockLease>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Banking.Feed.IBankFeedProvider>(),
            _services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<
                Harborline.Api.LocalNodeHost.Data.Banking.NodeLocalBankFeedDbContext>>());
        _sharedApp.MapApiRoutes(app => BankAccountRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            banking,
            _activeTeam,
            _services.GetRequiredService<NodeBankAccountWriter>(),
            _services.GetRequiredService<TimeProvider>()));

        _logger.LogInformation(
            "Node-local banking API registered over the recoverable local-node store " +
            "(accounts CRUD + import-statement + match-proposals/accept/un-match + reconciliation lock/unlock " +
            "+ mock feed connect/pull/disconnect under {RouteBase}).",
            BankAccountRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
