using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Governance;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local instance-lifecycle routes (<see cref="LifecycleRoutes"/>) onto
/// the shared Kestrel listener (ADR 0144 AD.1 setup-phase mechanics, slice B5). Exposes
/// <c>GET /api/local-node/governance/lifecycle</c> (phase + workshop-unlock decision) and
/// <c>POST /api/local-node/governance/lifecycle/finish-setup</c> (the founder-declared setup → operating
/// transition).
/// </summary>
/// <remarks>
/// A thin <see cref="IHostedService"/> wrapper; the route mapping lives in <see cref="LifecycleRoutes.Map"/>
/// (the single source of truth shared with the route tests so the wire contract has no test/prod drift).
/// Registered BEFORE <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps the paths while the
/// shared app is still pre-<c>StartAsync</c> — the same registration-order discipline as
/// <see cref="HostedSyncStatusApiEndpoint"/>.
/// </remarks>
public sealed class HostedLifecycleApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ITenantGovernanceStateStore _store;
    private readonly INodeWorkshopUnlockAuthority _unlockAuthority;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly TimeProvider _timeProvider;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly ILogger<HostedLifecycleApiEndpoint> _logger;

    /// <summary>Constructs the hosted lifecycle API endpoint.</summary>
    public HostedLifecycleApiEndpoint(
        SharedHostedWebApp sharedApp,
        ITenantGovernanceStateStore store,
        INodeWorkshopUnlockAuthority unlockAuthority,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        NodeCallerSessionToken callerAuth,
        ILogger<HostedLifecycleApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _unlockAuthority = unlockAuthority ?? throw new ArgumentNullException(nameof(unlockAuthority));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _callerAuth = callerAuth ?? throw new ArgumentNullException(nameof(callerAuth));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            LifecycleRoutes.Map(
                app.MapSelectedSessionProductGroup(),
                _store,
                _unlockAuthority,
                _activeTeam,
                _timeProvider,
                _callerAuth));

        _logger.LogInformation(
            "Node-local instance-lifecycle API registered (GET {RouteBase}; POST {FinishSetup}) — the ADR " +
            "0144 AD.1 setup-phase surface the Harborline app's lifecycle-aware Build fold reads; inc-4 " +
            "caller-auth enforced={CallerAuthEnforced}.",
            LifecycleRoutes.RouteBase, LifecycleRoutes.FinishSetupRoute, _callerAuth.IsEnforced);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
