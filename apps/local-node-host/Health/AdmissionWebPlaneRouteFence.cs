using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Admits desktop-plane calls and web-plane calls whose durable selected-session identity positively
/// resolves as founder.
/// </summary>
/// <remarks>
/// <b>The scope of the word "positive", stated precisely.</b> A desktop request delegates only when the
/// listener has published <see cref="DesktopPlaneRequestFeature"/> after authenticating the bootstrap
/// bearer or legacy founder credential. A selected-session request admits only on a durable read that
/// resolves founder membership; member, unresolved, absent, missing-authority and thrown reads all refuse.
/// <para>
/// <b>Missing attribution is neither branch.</b> A request with no desktop feature and no bound web or
/// device principal is refused with <see cref="WebPlaneUnavailableRouteFence.UnavailableCode"/>. A new
/// listener path cannot gain roster authority merely by forgetting to publish its caller attribution.
/// </para>
/// <para>
/// <b>Why this surface is fail-closed.</b> The admission family creates invitations, redeems and joins
/// into the roster, and signs admission material with the node key. Missing attribution could otherwise
/// mint genesis-admitter authority. That consequence requires positive desktop evidence even though a
/// bound selected-session founder remains an allowed web caller.
/// </para>
/// <para> Admitting a roster member is not minting a first principal, so this route does
/// not violate ADR 0161 D1. The founder membership is read from durable state; it is never derived from
/// the operating-system user name or any other OS-user signal, which would violate ADR 0161 D0.
/// (That is deliberately spelled out in prose: the legacy-authority debt scanner matches the symbol
/// itself, so citing it here — even to forbid it — would register this file as carrying static-actor
/// debt it does not have.)
/// </para>
/// </remarks>
internal static class AdmissionWebPlaneRouteFence
{
    internal const string FounderOnlyCode = "web-plane.admission.founder-only";

    internal static RouteGroupBuilder MapFounderWebAdmissionGroup(
        this IEndpointRouteBuilder app,
        IWebSelectedSessionIdentityAuthority? identityAuthority)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(string.Empty);
        RouteFenceMetadata.InstallFounderWebAdmission(
            group,
            identityAuthority);
        return group;
    }

    internal static async ValueTask<object?> RequireFounderForWebPlaneAsync(
        IWebSelectedSessionIdentityAuthority? identityAuthority,
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.HttpContext.Features.Get<DesktopPlaneRequestFeature>() is not null)
        {
            return await next(context).ConfigureAwait(false);
        }

        if (!NodeCallerAttributionScope.HasBoundWebPrincipal)
        {
            return Results.Json(
                new { code = WebPlaneUnavailableRouteFence.UnavailableCode },
                statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            var principal = context.HttpContext.Features.Get<SelectedSessionRequestPrincipal>();
            var handle = context.HttpContext.Request.Cookies[WebSessionCookieNames.Selected];
            if (identityAuthority is not null && principal is not null && !string.IsNullOrWhiteSpace(handle))
            {
                var identity = await identityAuthority
                    .DescribeAsync(handle, principal, context.HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                if (identity?.Membership == SelectedSessionMembership.Founder)
                {
                    return await next(context).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Membership is a positive assertion. Any read failure closes the gate.
        }

        return Results.Json(
            new { code = FounderOnlyCode },
            statusCode: StatusCodes.Status403Forbidden);
    }
}

internal sealed partial record RouteFenceMetadata
{
    internal static void InstallFounderWebAdmission(
        RouteGroupBuilder group,
        IWebSelectedSessionIdentityAuthority? identityAuthority)
    {
        Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> fenceFactory =
            (_, next) => context => AdmissionWebPlaneRouteFence.RequireFounderForWebPlaneAsync(
                identityAuthority,
                context,
                next);

        ((IEndpointConventionBuilder)group).Add(
            endpoint => endpoint.FilterFactories.Add(fenceFactory));
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            if (!endpoint.FilterFactories.Contains(fenceFactory))
            {
                throw new RouteFenceViolationException(
                    $"FounderWebAdmission route fence filter was removed from endpoint " +
                    $"'{endpoint.DisplayName ?? "<unnamed>"}'.");
            }

            endpoint.Metadata.Add(new RouteFenceMetadata(RouteFenceKind.FounderWebAdmission));
        });
    }
}
