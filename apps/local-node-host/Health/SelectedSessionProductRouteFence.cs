using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Makes a route family available to positively attributed desktop, selected-session, or
/// single-host-trusted callers. Device and unattributed callers refuse.
/// </summary>
internal static class SelectedSessionProductRouteFence
{
    internal const string UnavailableCode =
        "route-audience.selected-session-product.unavailable";

    internal static RouteGroupBuilder MapSelectedSessionProductGroup(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(string.Empty);
        RouteFenceMetadata.InstallSelectedSessionProduct(group);
        return group;
    }

    internal static async ValueTask<object?> RequireSelectedSessionProductAudienceAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var availability = context.HttpContext.GetEndpoint()?
            .Metadata.GetMetadata<PackInstallRoutes.PostInstallRouteAvailabilityMetadata>();
        if (availability is not null && !availability.IsAvailable())
        {
            return Results.NotFound();
        }

        var features = context.HttpContext.Features;
        if (features.Get<DesktopPlaneRequestFeature>() is not null ||
            features.Get<SelectedSessionRequestPrincipal>() is not null ||
            features.Get<SingleHostTrustedRequestFeature>() is not null)
        {
            return await next(context).ConfigureAwait(false);
        }

        return Results.Json(
            new { code = UnavailableCode },
            statusCode: StatusCodes.Status403Forbidden);
    }
}

internal sealed partial record RouteFenceMetadata
{
    internal static void InstallSelectedSessionProduct(RouteGroupBuilder group)
    {
        Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> fenceFactory =
            (_, next) => context =>
                SelectedSessionProductRouteFence.RequireSelectedSessionProductAudienceAsync(
                    context,
                    next);

        ((IEndpointConventionBuilder)group).Add(
            endpoint => endpoint.FilterFactories.Add(fenceFactory));
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            if (!endpoint.FilterFactories.Contains(fenceFactory))
            {
                throw new RouteFenceViolationException(
                    $"SelectedSessionProduct route fence filter was removed from endpoint " +
                    $"'{endpoint.DisplayName ?? "<unnamed>"}'.");
            }

            endpoint.Metadata.Add(new RouteFenceMetadata(RouteFenceKind.SelectedSessionProduct));
        });
    }
}
