using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Capabilities;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Classifies an operational or session-bootstrap family that the listener's exact pre-caller-auth
/// policy already exposes. The runtime filter delegates because listener admission plus the
/// handler's own admission is the enforcement promised by this audience.
/// </summary>
internal static class PreAuthOperationalRouteFence
{
    internal static RouteGroupBuilder MapPreAuthOperationalGroup(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(string.Empty);
        RouteFenceMetadata.InstallPreAuthOperational(group);
        return group;
    }

    internal static ValueTask<object?> DelegateAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        return next(context);
    }
}

internal sealed partial record RouteFenceMetadata
{
    internal static void InstallPreAuthOperational(RouteGroupBuilder group)
    {
        Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> fenceFactory =
            (_, next) => context => PreAuthOperationalRouteFence.DelegateAsync(context, next);

        ((IEndpointConventionBuilder)group).Add(
            endpoint => endpoint.FilterFactories.Add(fenceFactory));
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            if (!endpoint.FilterFactories.Contains(fenceFactory))
            {
                throw new RouteFenceViolationException(
                    $"PreAuthOperational route fence filter was removed from endpoint " +
                    $"'{endpoint.DisplayName ?? "<unnamed>"}'.");
            }

            if (endpoint is not RouteEndpointBuilder routeEndpoint)
            {
                throw new RouteFenceViolationException(
                    $"PreAuthOperational endpoint '{endpoint.DisplayName ?? "<unnamed>"}' has no " +
                    "route pattern that can be checked against the listener allowlist.");
            }

            string disposition;
            try
            {
                disposition = NodeListenerCallerAuthPolicy.DispositionFor(
                    routeEndpoint.RoutePattern);
            }
            catch (InvalidOperationException)
            {
                throw new RouteFenceViolationException(
                    $"PreAuthOperational route '{routeEndpoint.RoutePattern.RawText}' is not " +
                    "provably inside the listener pre-caller-auth allowlist.");
            }

            if (!disposition.Equals(
                    NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy,
                    StringComparison.Ordinal))
            {
                throw new RouteFenceViolationException(
                    $"PreAuthOperational route '{routeEndpoint.RoutePattern.RawText}' is outside " +
                    "the listener pre-caller-auth allowlist.");
            }

            endpoint.Metadata.Add(new RouteFenceMetadata(RouteFenceKind.PreAuthOperational));
        });
    }
}
