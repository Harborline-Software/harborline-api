using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The canonical DI registration of <see cref="LocalNodeWorker"/> and its two facets — the hosted-service role
/// AND the readable <see cref="IEnrollmentRebindStatus"/> — kept in ONE place so the composition root and the
/// host-boot smoke test register them IDENTICALLY (cerebrum [2026-06-21] DECISIVE cross-machine verify —
/// BLOCKER-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The cycle this fixes (BLOCKER-1).</b> The prior wiring registered the worker ONLY via
/// <c>AddHostedService&lt;LocalNodeWorker&gt;()</c> and then resolved <see cref="IEnrollmentRebindStatus"/> through
/// <c>sp.GetServices&lt;IHostedService&gt;().First(s =&gt; s is LocalNodeWorker)</c>. Because
/// <see cref="HostedSyncStatusApiEndpoint"/> is ITSELF an <see cref="IHostedService"/> and takes an optional
/// <see cref="IEnrollmentRebindStatus"/>, the generic host's enumeration of <c>IEnumerable&lt;IHostedService&gt;</c>
/// at <c>StartAsync</c> re-entered that very enumeration through the factory → a circular dependency that crashed
/// <c>Host.StartAsync</c> UNCONDITIONALLY on both platforms (the host did not boot). NO test booted the real host,
/// so it slipped past everything.
/// </para>
/// <para>
/// <b>The fix.</b> Register <see cref="LocalNodeWorker"/> ONCE as a concrete singleton, then bridge BOTH facets
/// to that single instance: the hosted-service role and the readable interface. Neither bridge enumerates
/// <c>IEnumerable&lt;IHostedService&gt;</c>, so there is no cycle. Both facets observe the SAME worker instance
/// (so the sync-status route reads the live rebind outcome).
/// </para>
/// </remarks>
public static class LocalNodeWorkerRegistration
{
    /// <summary>
    /// Register <see cref="LocalNodeWorker"/> as a single concrete singleton and bridge both its hosted-service
    /// role and its <see cref="IEnrollmentRebindStatus"/> facet to that one instance — the cycle-free wiring.
    /// </summary>
    public static IServiceCollection AddLocalNodeWorkerAndRebindStatus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ONE concrete singleton — both facets bridge to it (no IEnumerable<IHostedService> enumeration ⇒ no cycle).
        services.AddSingleton<LocalNodeWorker>();
        services.AddHostedService<LocalNodeWorker>(sp => sp.GetRequiredService<LocalNodeWorker>());
        services.AddSingleton<IEnrollmentRebindStatus>(sp => sp.GetRequiredService<LocalNodeWorker>());
        return services;
    }
}
