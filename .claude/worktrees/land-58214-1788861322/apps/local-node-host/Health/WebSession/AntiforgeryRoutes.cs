using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Browser-bound antiforgery bootstrap for anonymous and selected-session audiences.</summary>
internal static class AntiforgeryRoutes
{
    internal const string IssuePath = "/api/session/antiforgery";

    private sealed record ErrorResponse(string Error, string Message);

    internal static void Map(IEndpointRouteBuilder app, IWebAntiforgeryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(policy);
        app.MapGet(
            IssuePath,
            (Func<HttpContext, Task<IResult>>)(context => IssueAsync(policy, context)));
    }

    /// <remarks>
    /// <para>
    /// <b>A refusal EXPIRES the dead audience cookie (earlier repository ticket #3329 deep review).</b> Issuance fails
    /// whenever the browser presents an audience cookie that no longer resolves — a selected session past
    /// its 30-minute idle timeout, or a challenge cookie stranded by a failed select. Refusing alone left
    /// the browser WEDGED: the cookies are <c>HttpOnly</c> so the client cannot clear them,
    /// <c>HasAnyAuthenticatedAudience</c> keeps refusing the anonymous path while one is presented, and the
    /// only routes that delete them are logout (which needs a token this route would not issue) and
    /// select-on-success (which needs a token too). The selected cookie's <c>Expires</c> is the ABSOLUTE
    /// 8-hour lifetime, not the 30-minute idle one, so the everyday case — close a laptop, come back after
    /// 45 minutes — locked a human out of their own installation for most of a working day, behind a
    /// deliberately non-enumerating "check your credentials".
    /// </para>
    /// <para>
    /// So a refusal now clears what it refused on. The response carries the deletions, the browser drops
    /// them, and the client's ONE retry issues anonymously against a clean slate. This is a recovery path,
    /// not a weakening: nothing is issued on this response, the caller still gets 401, and the only cookies
    /// touched are ones the authority has just declared unusable. <c>Installation</c> is deliberately left
    /// alone — it is not session state and this route is not the place to decide its fate.
    /// </para>
    /// <para>
    /// Fail-closed ordering matters here. Eviction happens ONLY after the authority has refused, never
    /// speculatively, so a live session is never cleared by a request that merely raced it.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> IssueAsync(
        IWebAntiforgeryPolicy policy,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var selectedHandle = context.Request.Cookies[WebSessionCookieNames.Selected];
        var hasSelected = !string.IsNullOrWhiteSpace(selectedHandle);
        var issued = hasSelected
            ? await policy.RotateSelectedAsync(context, selectedHandle!).ConfigureAwait(false)
            : await policy.IssueAnonymousAsync(context).ConfigureAwait(false);
        if (issued)
        {
            return Results.NoContent();
        }

        EvictDeadAudienceCookies(context, hasSelected);
        return Results.Json(
            new ErrorResponse("antiforgery_audience_invalid", "Antiforgery issuance failed."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Expire the audience cookies that just blocked issuance, so the caller's retry is not refused for the
    /// same reason forever. Deleting a cookie the request did not carry is a no-op, so this is unconditional
    /// on the two session-scoped names.
    /// </summary>
    private static void EvictDeadAudienceCookies(HttpContext context, bool hadSelected)
    {
        // The selected handle failed to resolve to a live session, so it is spent. Drop the challenge with
        // it: a stranded challenge on its own also refuses the anonymous path, and it is a 5-minute
        // transient that costs nothing to re-mint.
        if (hadSelected)
        {
            context.Response.Cookies.Delete(
                WebSessionCookieNames.Selected,
                WebSessionCookieNames.ForDeletion());
        }

        context.Response.Cookies.Delete(
            WebSessionCookieNames.Challenge,
            WebSessionCookieNames.ForDeletion());
    }
}
