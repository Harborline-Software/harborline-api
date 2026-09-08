using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Startup half of the pack seed projection: on boot, re-projects the active team's ACTIVE installed
/// packs into the live domain registries (via <see cref="IPackSeedProjector"/>). Must be registered
/// AFTER <c>MultiTeamBootstrapHostedService</c> (so an active team is resolvable) and after the pack
/// install store + entity-type registry are composed.
/// </summary>
/// <remarks>
/// <para>
/// The install store is durable, so this startup pass restores every ACTIVE pack's projections after a
/// restart. Form projection is awaited because schema registration and form publication are asynchronous;
/// an interrupted register-then-publish sequence is resumed from its matching draft.
/// </para>
/// <para>
/// <b>Defensive.</b> If no active team is resolvable yet, it logs and returns — it never faults node
/// startup over a derived-state projection.
/// </para>
/// </remarks>
internal sealed class PackSeedProjectionHostedService : IHostedService
{
    private readonly IPackProjectionReconciler _reconciler;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<PackSeedProjectionHostedService> _logger;

    /// <summary>Constructs the startup projection service.</summary>
    public PackSeedProjectionHostedService(
        IPackInstaller installer,
        IActiveTeamAccessor activeTeam,
        ILogger<PackSeedProjectionHostedService> logger)
        : this(
            installer as IPackProjectionReconciler
                ?? throw new InvalidOperationException("The composed pack installer cannot reconcile projections."),
            activeTeam,
            logger)
    {
    }

    internal PackSeedProjectionHostedService(
        IPackProjectionReconciler reconciler,
        IActiveTeamAccessor activeTeam,
        ILogger<PackSeedProjectionHostedService> logger)
    {
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _reconciler.ReconcilePending(cancellationToken);
            _logger.LogInformation(
                "PackSeedProjectionHostedService: reconciled pending admitted pack transitions for every tenant.");
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // StartAsync must NOT throw (a derived-state projection may never brick node boot — see the
            // class remarks), but a swallowed failure must be LOUD, not a warning-level shrug (ticket 160).
            _logger.LogError(
                ex, "PackSeedProjectionHostedService: startup pack projection FAILED — continuing node boot; "
                + "the next projection pass can retry derived pack content.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
