using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.Kernel.Audit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the desktop-only authorization administration API before Kestrel starts.</summary>
public sealed class HostedAuthorizationAdminApiEndpoint(
    SharedHostedWebApp sharedApp,
    IRoleVocabularyReader vocabulary,
    IAuthorizationDefinitionCatalogueReader definitions,
    AuthorizationDefinitionWriter writer,
    IStandingRuleDefinitionStore standingRules,
    StandingCatalogue standings,
    IActiveTeamAccessor activeTeam,
    TimeProvider timeProvider,
    ILogger<HostedAuthorizationAdminApiEndpoint> logger,
    IAuditTrail? auditTrail = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sharedApp.MapApiRoutes(app => AuthorizationAdminRoutes.Map(
            app.MapDesktopPlaneOnlyGroup(),
            vocabulary,
            definitions,
            writer,
            standingRules,
            standings,
            activeTeam,
            timeProvider));
        sharedApp.MapApiRoutes(app => AuthorizationAdminRoutes.MapSelectedRoles(
            app.MapSelectedSessionProductGroup(), vocabulary, timeProvider));
        if (auditTrail is not null)
            sharedApp.MapApiRoutes(app => KernelAuditMetadataRoutes.Map(
                app.MapSelectedSessionProductGroup(), auditTrail, timeProvider));
        logger.LogInformation(
            "Desktop-plane-only authorization administration API registered at {RouteBase}.",
            AuthorizationAdminRoutes.RouteBase);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
