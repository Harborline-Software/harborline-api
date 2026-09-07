using Microsoft.Extensions.Hosting;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Maps the node-authoritative effective-permissions read in every deployed profile. Selected web
/// sessions resolve their canonical Party; the desktop bootstrap bearer resolves the active roster's
/// genesis Party. Listener authentication remains the authority that distinguishes those callers.
///
/// On a node that JOINED another team, the active roster's genesis is the ADMITTING node's founder,
/// not this node's own party — so a joined desktop node reports the admitter's owner permissions
/// rather than what its own admission granted. See the remarks on
/// <see cref="SelectedSessionIdentityRoutes.PermissionsAsync"/> for why that is survivable today
/// and why it is still wrong.
/// </summary>
internal sealed class HostedEffectivePermissionsApiEndpoint(
    SharedHostedWebApp sharedApp,
    IVerifiedTenantRosterReader rosterReader,
    IActiveTeamAccessor activeTeam) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sharedApp.MapApiRoutes(routes =>
            SelectedSessionIdentityRoutes.MapPermissions(
                routes.MapSelectedSessionProductGroup(),
                rosterReader,
                activeTeam));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
