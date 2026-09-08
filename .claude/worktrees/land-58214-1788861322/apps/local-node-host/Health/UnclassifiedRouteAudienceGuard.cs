using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Request-time safe default for executable endpoints that have not declared an enforced audience.
/// <see cref="RouteFenceMetadata"/> is the explicit-classification signal.
/// </summary>
internal static class UnclassifiedRouteAudienceGuard
{
    internal const string RefusalCode = "route-audience.unclassified";

    internal static void Use(IApplicationBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is null ||
                endpoint.Metadata.GetMetadata<RouteFenceMetadata>() is not null ||
                context.Features.Get<DesktopPlaneRequestFeature>() is not null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            logger.LogWarning(
                "Refused non-desktop request to unclassified endpoint {Endpoint} at {Path}.",
                endpoint.DisplayName ?? "<unnamed>",
                context.Request.Path.Value ?? string.Empty);
            await Results.Json(
                    new { code = RefusalCode },
                    statusCode: StatusCodes.Status403Forbidden)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        });
    }
}
