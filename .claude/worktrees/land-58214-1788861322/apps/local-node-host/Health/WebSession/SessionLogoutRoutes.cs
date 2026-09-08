using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The single owner of <c>POST /api/session/logout</c>. Server-authoritative sign-out for BOTH web
/// session audiences the node serves, dispatched on the audience the caller actually presents.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this dispatches (#3343).</b> This route was selected-audience-ONLY and returned 401 to a
/// founder holding the v1 <see cref="NodeWebSessionAuthority.SessionCookieName"/> cookie — which is
/// every founder on the current build, because no production path yet creates a v2 installation
/// account. Harborline App treats a non-2xx sign-out as "revocation not confirmed" and locks, so the
/// founder could not sign out at all AND the server-side session stayed live. The fix keys on the
/// AUDIENCE the holder actually has, not on adding a second route or a client-side guess: both
/// cookies are <c>HttpOnly</c>, so the browser cannot know which one it carries and cannot choose.
/// </para>
/// <para>
/// <b>Audience separation is preserved, not weakened.</b> Each handle is resolved through its OWN
/// authority. The selected branch never accepts a legacy cookie as a selected handle (it reads
/// <see cref="WebSessionCookieNames.Selected"/> only); the legacy branch never reaches the selected
/// authority or the selected antiforgery audience, and the legacy authority reads only its own
/// transports from the request. A caller presenting both is served by the SELECTED branch — the
/// stronger audience wins, so a v2 holder can never be downgraded onto the v1 path.
/// </para>
/// <para>
/// <b>A sign-out MAY expire both cookies, and that is the point.</b> Separation governs which
/// authority resolves which handle — it does NOT mean each branch leaves the other audience alone.
/// A cookie is expired exactly where its own authority confirmed revocation of the session behind
/// it, so a holder of both signs out of both. Do not restate this as "each branch expires only its
/// OWN cookie": that was true before the both-cookie survivor was fixed, and it is false now. A
/// selected-only sign-out that left a live legacy record was not a tidiness gap — once the selected
/// cookie was deleted the listener reclassified the browser as legacy-eligible and re-admitted it,
/// putting the user back into the app they had just signed out of.
/// </para>
/// <para>
/// <b>Antiforgery on the legacy branch — a KNOWN GAP, tracked, not a settled design.</b> The
/// selected branch consumes one-time selected-audience antiforgery state (ADR 0160 D7 / ADR 0099).
/// The legacy branch consumes nothing today, and AUTH-3 names logout explicitly, so this branch does
/// not meet that bar. Its only control is the cookie's own posture: <c>SameSite=Strict</c> +
/// <c>__Host-</c> + <c>Secure</c>, host-only, same-origin.
/// </para>
/// <para>
/// <b>What that leaves open.</b> <c>SameSite=Strict</c> blocks cross-SITE, and cookies are
/// PORT-AGNOSTIC — <c>__Host-</c> pins host-only, <c>Path=/</c> and <c>Secure</c>, but not port. So
/// another <c>https</c> origin on the same host is same-site and WILL have
/// <c>__Host-web_session</c> attached to its POST; on a local-first node another local service the
/// operator runs is a plausible neighbour. The harm is a forced sign-out — availability and
/// annoyance, not privilege escalation — but it is a real capability this route did not have while
/// it answered 401.
/// </para>
/// <para>
/// <b>Do not reinstate the old rationale.</b> An earlier revision argued the anonymous audience was
/// worthless because "an unauthenticated caller can mint one at will". That is not the property that
/// makes an antiforgery token a CSRF control. The control is that the attacker cannot get the
/// VICTIM's browser to send a token the attacker knows alongside the victim's binding cookie, and the
/// anonymous audience satisfies exactly that: the token is emitted only in a response header
/// (<see cref="WebAntiforgeryPolicy.HeaderName"/>) that a cross-origin page cannot read (the node
/// serves one origin with no CORS surface); it is browser-bound to an <c>HttpOnly</c>
/// <c>__Host-hl-antiforgery</c> cookie a cross-site page cannot set; and a form POST or
/// <c>no-cors</c> fetch cannot set a custom request header at all. A v1-only holder is precisely the
/// caller for whom <c>ConsumeAnonymousAsync</c> is available, because
/// <c>HasAnyAuthenticatedAudience</c> returns false for them. So a usable control does exist here.
/// </para>
/// <para>
/// <b>Why it is not applied in this change.</b> Requiring the token server-side alone would break the
/// only sign-out the product actually issues: <c>webSessionClient.ts</c> posts to this route with no
/// request headers at all, so every Harborline App logout would 400 — re-breaking the exact founder-cannot-
/// sign-out defect this route exists to fix. Both halves must land together, and they are carded as
/// earlier repository ticket #3353 (with the pre-existing selected-branch gap folded in: the same client sends no token
/// on the v2 path either, so that branch has never succeeded end-to-end from the product).
/// </para>
/// </remarks>
internal static class SessionLogoutRoutes
{
    internal const string LogoutPath = "/api/session/logout";

    private sealed record ErrorResponse(string Error, string Message);

    internal static void Map(
        IEndpointRouteBuilder app,
        IWebSelectedSessionLogoutAuthority selectedAuthority,
        INodeWebSessionAuthority legacyAuthority,
        IWebAntiforgeryPolicy antiforgery)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(selectedAuthority);
        ArgumentNullException.ThrowIfNull(legacyAuthority);
        ArgumentNullException.ThrowIfNull(antiforgery);
        // Explicit Delegate cast: a HttpContext-only lambda otherwise binds the
        // RequestDelegate overload, which discards the Task<IResult> (ASP0016).
        app.MapPost(
            LogoutPath,
            (Delegate)((HttpContext context) =>
                LogoutAsync(selectedAuthority, legacyAuthority, antiforgery, context)));
    }

    /// <summary>
    /// Dispatch on the presented audience: selected-session first (the stronger audience wins), then
    /// the legacy web session. A request carrying neither is refused.
    /// </summary>
    internal static async Task<IResult> LogoutAsync(
        IWebSelectedSessionLogoutAuthority selectedAuthority,
        INodeWebSessionAuthority legacyAuthority,
        IWebAntiforgeryPolicy antiforgery,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(selectedAuthority);
        ArgumentNullException.ThrowIfNull(legacyAuthority);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";

        var selectedHandle = context.Request.Cookies[WebSessionCookieNames.Selected];
        return string.IsNullOrWhiteSpace(selectedHandle)
            ? await LegacyLogoutAsync(legacyAuthority, context).ConfigureAwait(false)
            : await SelectedLogoutAsync(
                    selectedAuthority,
                    legacyAuthority,
                    antiforgery,
                    context,
                    selectedHandle)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// The v2 selected-audience branch: one-time selected antiforgery, the coordinated
    /// selected-session revocation, an attribute-matching expiry of the selected cookie — and then,
    /// if the same browser also presents a legacy credential, revocation of THAT session too.
    /// </summary>
    /// <remarks>
    /// Signing out of only the selected audience is not signing out. While the selected cookie is
    /// present, <c>SharedHostedWebApp.ClassifyWebCookieAudience</c> returns <c>Selected</c>, so the
    /// legacy record is never consulted and looks dormant. The moment this branch deletes that
    /// cookie the classification falls to <c>LegacyEligible</c> and the listener's accept path
    /// authenticates the surviving legacy record — so the browser is re-admitted, and the Harborline App's
    /// boot <c>whoami</c> puts the user straight back into the app they just signed out of.
    /// Revoking both is not a weakening of audience separation: each handle is still resolved by
    /// its OWN authority, which is what separation means. The selected handle never reaches the
    /// legacy authority, and the legacy authority reads only its own transports from the request.
    /// </remarks>
    private static async Task<IResult> SelectedLogoutAsync(
        IWebSelectedSessionLogoutAuthority authority,
        INodeWebSessionAuthority legacyAuthority,
        IWebAntiforgeryPolicy antiforgery,
        HttpContext context,
        string selectedHandle)
    {
        if (!await antiforgery.ConsumeSelectedAsync(context, selectedHandle).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!await authority.LogoutAsync(selectedHandle, context.RequestAborted).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("logout_failed", "Session logout could not be confirmed."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        context.Response.Cookies.Delete(
            WebSessionCookieNames.Selected,
            WebSessionCookieNames.ForDeletion());

        // Leave no audience live behind the sign-out. The legacy authority reads its own transports
        // from the request and reports false when this browser presents none, so this is a no-op for
        // a selected-only holder.
        if (await legacyAuthority.LogoutAsync(context, context.RequestAborted).ConfigureAwait(false))
        {
            legacyAuthority.ClearSessionCookie(context);
        }
        return Results.NoContent();
    }

    /// <summary>
    /// The v1 legacy branch: revoke the presented web session in the server-side session store, then
    /// expire the legacy cookie with matching attributes.
    /// </summary>
    /// <remarks>
    /// A 204 asserts exactly one thing, and it is the thing sign-out means: the credential this
    /// request presented resolves to NO live session server-side. That holds whether the record was
    /// just removed or had already lapsed, which makes the operation idempotent — a retry after a
    /// partially-failed sign-out confirms instead of refusing forever, so Harborline App's locked state can
    /// recover without a reload. It is also the non-enumerating answer: a 401-for-unknown /
    /// 204-for-known split would turn this route into a session-id oracle.
    /// </remarks>
    private static async Task<IResult> LegacyLogoutAsync(
        INodeWebSessionAuthority authority,
        HttpContext context)
    {
        if (!await authority.LogoutAsync(context, context.RequestAborted).ConfigureAwait(false))
        {
            // No credential of any audience was presented — there is nothing to sign out of, and no
            // cookie is mutated (mirrors the selected branch's refusal posture).
            return Results.Json(
                new ErrorResponse("no_session", "No active session."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        authority.ClearSessionCookie(context);
        return Results.NoContent();
    }
}
