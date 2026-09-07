using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Capabilities;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Makes a whole route family available only when listener admission positively identifies the desktop
/// operator. Routes mapped into the returned group refuse missing, web, and device attribution, per
/// ADR 0160 D5: "every consequential web route either consumes the request principal or is unavailable."
/// </summary>
/// <remarks>
/// <para>
/// <b>The family this exists for, and why the authorization fence cannot reach it.</b>
/// The web-plane authorization fence (card #3356) gated a SEAM — code that ASKED an authorization question
/// by resolving <see cref="Harborline.Api.Foundation.Authorization.IAuthorizationContext"/>. (Ticket 205
/// converted every such route to the point-of-use gate and the seam decorator is gone; the reasoning below
/// is why this fence was never covered by it, and still is not.) A route family that instead
/// CAPTURES the operator's identity once at startup and mints per-request authority from the captured
/// value never asks, so no amount of hardening that decorator can see it. Measured, not argued: with
/// #3356 merged, a selected-session member's <c>POST /api/local-node/forms/{id}/submit</c> still
/// succeeded or was denied purely according to the role the OS operator held when the host booted
/// (<c>FormsStartupCapturedIdentityFenceTests</c>). A family that has already left the seam has to be
/// closed at the route, which is what this does.
/// </para>
/// <para>
/// <b>Why a group filter and not a per-handler guard.</b> The same gate-all-by-default reasoning as the
/// listener's caller-auth and as #3356's single-seam decorator: a per-route guard is the forgotten-route
/// problem, where the next route added to the family is open until someone remembers it.
/// </para>
/// <para>
/// <b>WHERE to install it, and the trap in the obvious answer.</b> Two placements are in use, and they are
/// not interchangeable. The forms family installs the fence at the hosted endpoint that performs the
/// startup capture, in the same call that maps the routes. The admission family installs it inside
/// <c>AdmissionRoutes.MapCore</c> — the private funnel both public overloads delegate to — because its
/// hosted endpoint has TWO mapping branches (with and without the pairing bundle) and production takes the
/// branch a test harness is least likely to construct. A fence at that caller is only ever proven for
/// whichever branch the harness built, and #3382's first revision shipped exactly that gap: green tests,
/// one arm unfenced.
/// </para>
/// <para>
/// So: <b>when the caller maps unconditionally, install at the caller; when the caller branches, install at
/// the funnel every branch routes through.</b> Do not "restore consistency" by moving a funnel-installed
/// fence back up to its caller — that is the regression, not the tidy-up.
/// </para>
/// <para>
/// <b>Refusal, never a permission decision.</b> Resolving the member's real permissions is MTW-3 (CIC
/// 2026-07-29: MTW-2's bar is sign in and be identified). This only ever makes a surface more closed;
/// it adds no allow path.
/// </para>
/// <para>
/// <b>The plane signal is positive and request-local.</b> The listener publishes
/// <see cref="DesktopPlaneRequestFeature"/> only after its bootstrap bearer or legacy founder authority
/// succeeds. Selected-session and device branches never publish it. A new accept path therefore starts
/// refused until it explicitly proves that it represents the desktop operator; forgetting attribution
/// cannot turn into operator authority.
/// </para>
/// <para>
/// <b>Consumer decisions.</b> Forms and form definitions can mint capabilities from a role captured at
/// startup; drafts resolve party state from the outer container; comms stamps the captured roster author
/// and signs with the node key; founder binding can irreversibly name the installation root. Each remains
/// desktop-only until it has request-principal authorization of its own, so all require this positive
/// assertion. Admission has a distinct founder-standing policy but shares the same missing-attribution
/// refusal in <see cref="AdmissionWebPlaneRouteFence"/> because it mutates the roster and signs admission
/// material with the node key. The remaining direct HTTP consumers of the node operation signer are the
/// current-principal attestation and the two pack-authoring surfaces; each emits portable node-signed
/// material and is therefore mapped through this group by its hosted endpoint.
/// </para>
/// <para>
/// <b>Signing audit boundary.</b> <c>KernelAuditPackInstallAudit</c> signs internal evidence after an
/// independently authorized pack mutation; the HTTP route receives neither the signer nor caller-selected
/// material to sign. It is not a node-key-signing surface in this classification. Admission, comms,
/// current-principal attestation, pack export, and pack compose/export are the direct HTTP signing surfaces.
/// </para>
/// </remarks>
internal static class WebPlaneUnavailableRouteFence
{
    /// <summary>
    /// The stable machine code returned to a refused web-plane caller. The client localizes it; the
    /// wire never carries English.
    /// </summary>
    internal const string UnavailableCode = "web-plane.route.unavailable";

    /// <summary>
    /// Opens a route group whose every endpoint — present and future — requires a positive desktop-plane
    /// assertion. Map the family's routes onto the returned builder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What counts as desktop.</b> The bootstrap bearer and Accept-3 legacy web-client session publish
    /// the assertion only after authentication. Accept-3 remains deliberate: it authenticates the single
    /// configured founder credential, so it legitimately represents the desktop operator, and its storage
    /// is separated from selected-session handles. Un-enforced listener mode and requests that bypass the
    /// listener publish nothing and are refused by this consequential-route fence.
    /// </para>
    /// <para>
    /// <b>Membership is separate from request polarity.</b> This filter reaches only endpoints mapped onto
    /// the returned builder. A sibling family sharing a route base but registered elsewhere would not
    /// inherit it, so each endpoint is stamped with queryable metadata and the executable-endpoint
    /// registry refuses startup when a fenced route base contains an unstamped route.
    /// </para>
    /// <para>
    /// Membership is stamped as queryable <see cref="RouteFenceMetadata"/> beside the filter factory;
    /// live refusal tests separately prove that the filter still enforces the policy.
    /// </para>
    /// </remarks>
    internal static RouteGroupBuilder MapDesktopPlaneOnlyGroup(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // An empty prefix: the group exists to carry the filter, not to move any route. The mapped
        // paths are byte-identical to mapping them on `app` directly.
        var group = app.MapGroup(string.Empty);
        RouteFenceMetadata.InstallDesktopPlaneOnly(group);
        return group;
    }

    /// <summary>
    /// The filter itself: delegate only on a positively asserted desktop plane.
    /// </summary>
    internal static async ValueTask<object?> RequireDesktopPlaneAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.HttpContext.Features.Get<DesktopPlaneRequestFeature>() is null)
        {
            return Results.Json(
                new { code = UnavailableCode },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context).ConfigureAwait(false);
    }
}

internal sealed partial record RouteFenceMetadata
{
    internal static void InstallDesktopPlaneOnly(RouteGroupBuilder group)
    {
        Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> fenceFactory =
            (_, next) => context =>
                WebPlaneUnavailableRouteFence.RequireDesktopPlaneAsync(context, next);

        ((IEndpointConventionBuilder)group).Add(
            endpoint => endpoint.FilterFactories.Add(fenceFactory));
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            if (!endpoint.FilterFactories.Contains(fenceFactory))
            {
                throw new RouteFenceViolationException(
                    $"DesktopPlaneOnly route fence filter was removed from endpoint " +
                    $"'{endpoint.DisplayName ?? "<unnamed>"}'.");
            }

            endpoint.Metadata.Add(new RouteFenceMetadata(RouteFenceKind.DesktopPlaneOnly));
        });
    }
}
