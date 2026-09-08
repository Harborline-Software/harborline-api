using Microsoft.AspNetCore.Http;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>Builds one request-authenticated financial write context at the route boundary.</summary>
internal static class FinancialRouteWriteAuthority
{
    internal static AuthorizationWriteContext Create(
        HttpContext http,
        TenantId tenant,
        DateTimeOffset at) =>
        new(new ActorId(NodeCallerParty.Resolve(http).Value), tenant, at);

    internal static AuthorizationWriteContext Create(
        HttpContext http,
        TenantId tenant,
        TimeProvider timeProvider) =>
        Create(http, tenant, timeProvider.GetUtcNow());

    internal static AuthorizationWriteContext Create(
        HttpContext http,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider) =>
        Create(http, NodeTenant.Resolve(activeTeam), timeProvider);
}
