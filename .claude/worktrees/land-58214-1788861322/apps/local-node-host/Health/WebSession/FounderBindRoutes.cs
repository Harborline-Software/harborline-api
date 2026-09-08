using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Health;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Selected-audience-only installation founder binding — the ONE route that reaches
/// <see cref="InstallationFounderBindingService"/>.
/// </summary>
/// <remarks>
/// Deliberately NOT listed in <c>NodeListenerCallerAuthPolicy</c>'s pre-auth allowlist. It WAS admitted
/// by the listener gate's selected-cookie accept, which revalidates every owning account, membership,
/// Party, grant and epoch fact and publishes one immutable principal before the handler runs — and that
/// admission is exactly what the desktop-plane fence below now refuses, because it let ANY signed-in
/// account designate itself as the installation root. See the fence comment on <c>Map</c>: the route is
/// currently reachable by NO caller, deliberately, until #3490 gives the desktop plane an evidence path.
/// </remarks>
internal static class FounderBindRoutes
{
    internal const string BindPath = "/api/session/founder-bind";

    internal sealed record BindRequest(string? IdempotencyKey);

    private sealed record BindResponse(
        string AccountId,
        string TenantId,
        long OwnerVersion,
        DateTimeOffset DesignatedAtUtc,
        bool Replayed);

    private sealed record ErrorResponse(string Error, string Message);

    internal static void Map(
        IEndpointRouteBuilder app,
        IWebFounderBindAuthority authority,
        IWebAntiforgeryPolicy antiforgery)
    {
        // ⚠ SECURITY (card #3490, CIC ruling 2026-08-01) — DESKTOP PLANE ONLY.
        //
        // This is the ONE ROUTE that can write InstallationIdentityRootDesignationRecord, the singleton
        // naming the installation's root, and it binds the CALLER's own account. WebFounderBindAuthority
        // "adds no policy of its own", and the handler checks only that SOME selected-session principal
        // exists. So on an installation with no designation yet, the FIRST signed-in account to post here
        // becomes the root — first-come, first-served — and InstallationFounderBindingService.BindAsync
        // refuses every later attempt as AlreadyDesignated. There is no repair path in the tree.
        // (The bootstrap ceremony also writes the record, but only during first setup.)
        //
        // Not hypothetical: every installation that bootstrapped before #3467 is in that state, because
        // the designating ceremony runs once at bootstrap and theirs already ran without it. Customer-zero
        // has two accounts and no designation, so its joiner could take the founder's place.
        //
        // ⚠ THIS IS AN INTERIM CLOSURE, NOT THE FIX FOR #3490 — and it leaves the route reachable by NO
        // caller. The handler requires a published SelectedSessionRequestPrincipal; the fence requires a
        // DesktopPlaneRequestFeature. The listener deliberately publishes those markers on disjoint accept
        // branches, so their intersection is empty.
        //
        // The desktop plane's 401 is NOT caused by this fence — it is identical without it, because the
        // desktop carries a bootstrap token that authenticates the HOST and never names an account. So
        // "whoever is at the machine may designate" is the INTENT and is not yet true; giving the desktop
        // plane an account-bearing evidence path is the remaining half of #3490.
        //
        // Landed anyway because the trade is the fail-closed one: a wrong designation is irreversible,
        // while an inability to designate is recoverable by that follow-up.
        //
        // Installed HERE rather than at the caller so un-fencing requires editing this class. #3382 shipped
        // a fence at its call site and a review found it proven for only one of two mapping branches.

        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(antiforgery);

        app = app.MapDesktopPlaneOnlyGroup();
        app.MapPost(
            BindPath,
            (BindRequest request, HttpContext context) =>
                BindAsync(authority, antiforgery, request, context));
    }

    internal static async Task<IResult> BindAsync(
        IWebFounderBindAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        BindRequest? request,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";

        var selectedHandle = context.Request.Cookies[WebSessionCookieNames.Selected];
        if (string.IsNullOrWhiteSpace(selectedHandle))
        {
            return Refused();
        }

        // Defense in depth: the listener gate publishes this feature before the handler runs, so a
        // missing principal means the route was reached off its intended path. Refuse identically.
        var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
        if (principal is null)
        {
            return Refused();
        }

        if (!await antiforgery.ConsumeSelectedAsync(context, selectedHandle).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        // earlier repository ticket #3311 — re-issue the spent token HERE, not on the success branch, so every exit
        // path below answers with a usable one. Rationale on IWebAntiforgeryPolicy.RotateSelectedAsync.
        _ = await antiforgery.RotateSelectedAsync(context, selectedHandle).ConfigureAwait(false);

        var outcome = await authority.BindAsync(
                new WebFounderBindRequest(
                    principal.AccountId,
                    principal.TenantId,
                    principal.PrincipalUserId,
                    principal.CanonicalParty,
                    principal.MembershipOwnerVersion,
                    request?.IdempotencyKey ?? string.Empty,
                    principal.CoordinationCorrelationId),
                context.RequestAborted)
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            WebFounderBindStatus.Bound or WebFounderBindStatus.Replayed =>
                Results.Ok(new BindResponse(
                    outcome.AccountId ?? principal.AccountId,
                    principal.TenantId.Value,
                    outcome.OwnerVersion,
                    outcome.DesignatedAtUtc ?? default,
                    outcome.Status is WebFounderBindStatus.Replayed)),
            WebFounderBindStatus.Conflict => Results.Json(
                new ErrorResponse(
                    "bind_conflict",
                    "The idempotency key was replayed with different founder evidence."),
                statusCode: StatusCodes.Status409Conflict),
            WebFounderBindStatus.AlreadyDesignated => Results.Json(
                new ErrorResponse(
                    "already_designated",
                    "This installation already has a founder designation."),
                statusCode: StatusCodes.Status409Conflict),
            WebFounderBindStatus.InvalidRequest => Results.Json(
                new ErrorResponse("bind_rejected", "The founder bind request was rejected."),
                statusCode: StatusCodes.Status400BadRequest),
            // AccountNotActive is deliberately indistinguishable from an unauthenticated refusal:
            // the response never reveals whether an installation account exists or is disabled.
            _ => Refused(),
        };
    }

    private static IResult Refused() =>
        Results.Json(
            new ErrorResponse("bind_failed", "Founder binding failed."),
            statusCode: StatusCodes.Status401Unauthorized);
}
