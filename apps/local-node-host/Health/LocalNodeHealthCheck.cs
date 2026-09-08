using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Gossip;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Wave 5.2.D health check exposed on the local-node-host <c>/health</c>
/// endpoint. Per <c>_shared/product/wave-5.2-decomposition.md</c> §6
/// "Health + Monitoring":
/// <list type="bullet">
///   <item><b>Healthy</b> — an active team is materialized and its gossip
///     daemon is running.</item>
///   <item><b>Degraded</b> — an active team is materialized but gossip has
///     not yet started (the typical shape during <see cref="LocalNodeWorker"/>
///     boot, between team materialization and <c>gossip.StartAsync</c>).</item>
///   <item><b>Unhealthy</b> — no active team has been materialized yet.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The check uses <see cref="IActiveTeamAccessor.Active"/> as the primary
/// signal because it reflects the paper §4 + §5.1 composition contract —
/// the host is useful to consumers only once a <see cref="TeamContext"/> has
/// been materialized and its team-scoped services (SQLCipher store, event
/// log, gossip) have been wired. A process that is running but has no active
/// team is, from a Bridge supervisor's perspective, indistinguishable from a
/// zombie child that failed to bootstrap; returning
/// <see cref="HealthStatus.Unhealthy"/> in that case gives the Wave 5.2.D
/// <c>TenantHealthMonitor</c> a deterministic "replace me" signal.
/// </para>
/// <para>
/// Gossip is resolved via <c>IActiveTeamAccessor.Active?.Services.GetService&lt;IGossipDaemon&gt;()</c>
/// rather than injected directly: gossip is team-scoped (Wave 6.3.E.2), so
/// the install-level provider does not expose it. If no gossip daemon is
/// registered at all the check still returns Healthy — a test host (see
/// <c>LocalNodeWorkerTests</c>) exercises exactly that shape, and the
/// Bridge tenant-spawn path always registers one.
/// </para>
/// </remarks>
public sealed class LocalNodeHealthCheck : IHealthCheck
{
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly Data.Roster.RosterCrdtProjection? _roster;

    /// <summary>
    /// Construct the health check. Called by the DI container once per probe
    /// request when registered via
    /// <c>services.AddHealthChecks().AddCheck&lt;LocalNodeHealthCheck&gt;(…)</c>.
    /// </summary>
    /// <param name="activeTeam">Install-level active-team accessor.</param>
    /// <param name="roster">Install-level roster refusal status.</param>
    public LocalNodeHealthCheck(IActiveTeamAccessor activeTeam, Data.Roster.RosterCrdtProjection? roster = null)
    {
        ArgumentNullException.ThrowIfNull(activeTeam);
        _activeTeam = activeTeam;
        _roster = roster;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var active = _activeTeam.Active;
        if (active is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No active team has been materialized yet. The host is up but has " +
                "not bootstrapped a team context; Bridge supervisor should treat this " +
                "as a failed boot."));
        }

        if (_roster?.RefusalReports is { Count: > 0 } reports)
            return Task.FromResult(HealthCheckResult.Degraded(string.Join(Environment.NewLine,
                reports.Select(r => $"Roster refused: {r.Code}. {r.Remediation}"))));

        var gossip = active.Services.GetService<IGossipDaemon>();
        if (gossip is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                $"Active team {active.TeamId} is materialized; no gossip daemon " +
                "registered (sync not enabled on this install)."));
        }

        if (!gossip.IsRunning)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Active team {active.TeamId} is materialized but its gossip daemon " +
                "is not running. Expected during boot between team materialization " +
                "and LocalNodeWorker.ExecuteAsync's gossip.StartAsync call."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Active team {active.TeamId} is materialized and gossip is running " +
            $"({gossip.KnownPeers.Count} known peer(s))."));
    }
}
