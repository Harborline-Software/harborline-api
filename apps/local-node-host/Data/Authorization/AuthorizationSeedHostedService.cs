using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Authorization;

/// <summary>Installs the validated fixed authorization definitions after database migration.</summary>
/// <remarks>
/// Ticket 294 slice 3b: the node-operator holding is granted to the desktop actor, the roster party bound to this
/// node's key (<see cref="NodeOperatorIdentity"/>). Before seeding, rows an older build keyed by the retired
/// "local" actor are rekeyed to it once (<see cref="RetiredDesktopActorRekey"/>), or the boot refuses.
/// </remarks>
internal sealed class AuthorizationSeedHostedService(
    AccessGrantAuthorizationSeed seed,
    IActiveTeamAccessor activeTeam,
    ITeamContextFactory teamContexts,
    AuthorizationSeedProfile profile,
    TimeProvider timeProvider,
    NodeOperatorIdentity? nodeOperator = null,
    IDbContextFactory<NodeLocalSearchDbContext>? grantRows = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var holder = nodeOperator?.Principal;
        if (grantRows is not null)
            await RetiredDesktopActorRekey.RunAsync(grantRows, holder, cancellationToken).ConfigureAwait(false);

        // Seeded grants are tenant-partitioned and the routes resolve their tenant from the CURRENTLY
        // active team, so seeding only the boot-active tenant loses the node operator's holdings the
        // moment the operator switches teams. Every team the multi-team bootstrap materialized is seeded
        // (InstallAsync is idempotent per tenant; the definitions are install-wide and written once).
        var at = timeProvider.GetUtcNow();
        foreach (var tenant in Tenants())
            await seed.InstallAsync(tenant, at, profile, holder, cancellationToken).ConfigureAwait(false);
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
