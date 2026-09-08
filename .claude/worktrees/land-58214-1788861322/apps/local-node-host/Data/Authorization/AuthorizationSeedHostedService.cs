using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>Installs the validated fixed authorization definitions after database migration.</summary>
internal sealed class AuthorizationSeedHostedService(
    AccessGrantAuthorizationSeed seed,
    IActiveTeamAccessor activeTeam,
    ITeamContextFactory teamContexts,
    AuthorizationSeedProfile profile,
    TimeProvider timeProvider) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seeded grants are tenant-partitioned and the routes resolve their tenant from the CURRENTLY
        // active team, so seeding only the boot-active tenant loses the node operator's holdings the
        // moment the operator switches teams. Every team the multi-team bootstrap materialized is seeded
        // (InstallAsync is idempotent per tenant; the definitions are install-wide and written once).
        var at = timeProvider.GetUtcNow();
        foreach (var tenant in Tenants())
            await seed.InstallAsync(tenant, at, profile, cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<TenantId> Tenants()
    {
        var seen = new HashSet<TenantId> { NodeTenant.Resolve(activeTeam) };
        foreach (var context in teamContexts.Active)
            seen.Add(ActiveTeamTenantContext.ProjectTenantId(context.TeamId));
        return seen;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
