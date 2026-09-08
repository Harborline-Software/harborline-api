using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Makes an already LAN-allowlisted product-data family available to desktop, selected-session,
/// single-host-trusted, and positively attributed LAN device callers.
/// </summary>
internal static class DeviceReachableProductDataRouteFence
{
    internal const string UnavailableCode =
        "route-audience.device-reachable-product-data.unavailable";

    internal static RouteGroupBuilder MapDeviceReachableProductDataGroup(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(string.Empty);
        RouteFenceMetadata.InstallDeviceReachableProductData(group);
        return group;
    }

    internal static async ValueTask<object?> RequireDeviceReachableProductDataAudienceAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var features = context.HttpContext.Features;
        if (features.Get<DesktopPlaneRequestFeature>() is not null ||
            features.Get<SelectedSessionRequestPrincipal>() is not null ||
            features.Get<SingleHostTrustedRequestFeature>() is not null ||
            features.Get<LanListenerRequestFeature>() is not null)
        {
            return await next(context).ConfigureAwait(false);
        }

        return Results.Json(
            new { code = UnavailableCode },
            statusCode: StatusCodes.Status403Forbidden);
    }

    internal static bool IsLanAllowlistedRoutePattern(RouteEndpointBuilder endpoint)
    {
        var routePattern = endpoint.RoutePattern.RawText ?? string.Empty;
        if (routePattern.Equals(
                LocalNodeLanRoutePolicy.PairingRedeemPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LocalNodeLanRoutePolicy.AllowlistedRoots.Any(root =>
            routePattern.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            routePattern.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed partial record RouteFenceMetadata
{
    internal static void InstallDeviceReachableProductData(RouteGroupBuilder group)
    {
        Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> fenceFactory =
            (_, next) => context =>
                DeviceReachableProductDataRouteFence.RequireDeviceReachableProductDataAudienceAsync(
                    context,
                    next);

        ((IEndpointConventionBuilder)group).Add(
            endpoint => endpoint.FilterFactories.Add(fenceFactory));
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            if (!endpoint.FilterFactories.Contains(fenceFactory))
            {
                throw new RouteFenceViolationException(
                    $"DeviceReachableProductData route fence filter was removed from endpoint " +
                    $"'{endpoint.DisplayName ?? "<unnamed>"}'.");
            }

            if (endpoint is not RouteEndpointBuilder routeEndpoint ||
                !DeviceReachableProductDataRouteFence.IsLanAllowlistedRoutePattern(routeEndpoint))
            {
                var route = endpoint is RouteEndpointBuilder builder
                    ? builder.RoutePattern.RawText
                    : endpoint.DisplayName;
                throw new RouteFenceViolationException(
                    $"DeviceReachableProductData route '{route ?? "<unnamed>"}' is outside the " +
                    "LocalNodeLanRoutePolicy LAN allowlist.");
            }

            endpoint.Metadata.Add(
                new RouteFenceMetadata(RouteFenceKind.DeviceReachableProductData));
        });
    }
}
