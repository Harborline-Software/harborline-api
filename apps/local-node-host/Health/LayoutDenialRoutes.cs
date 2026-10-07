using Harborline.Api.Foundation.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Layout;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>HTTP reader for the protected related-binding denial trail.</summary>
public static class LayoutDenialRoutes
{
    /// <summary>The canonical route for retrieving related-binding denials.</summary>
    public const string RouteBase = "/api/local-node/layout/denials";

    /// <summary>Maps the reader route, closing over dependencies from the outer host container.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        LayoutDenialReader reader,
        IActiveTeamAccessor activeTeam,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(time);

        app.MapGet(RouteBase, async (
            string? block_id,
            string? relationship_key,
            string? request_id,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(block_id)
                || string.IsNullOrWhiteSpace(relationship_key)
                || string.IsNullOrWhiteSpace(request_id))
            {
                return Results.BadRequest(new { title = "invalid_layout_denial_query" });
            }

            var tenant = NodeTenant.Resolve(activeTeam);
            var denials = await reader.ReadAsync(
                tenant,
                NodeGatePrincipal.Resolve(http),
                block_id,
                relationship_key,
                request_id,
                AdmittedInstant.Read(time),
                ct).ConfigureAwait(false);

            // The reader returns empty for both an absent denial and an unauthorized reader. Keep the
            // route's wire response identical so it cannot become an authorization oracle.
            return Results.Ok(denials);
        });
    }
}
