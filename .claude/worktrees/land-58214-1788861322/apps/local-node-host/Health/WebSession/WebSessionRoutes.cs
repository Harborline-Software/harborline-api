using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The node's WEB-CLIENT roster-session surface (<c>/api/session/*</c>). A browser user logs in
/// against the node's own credential (<c>POST login</c>) and receives an <c>HttpOnly</c> session
/// cookie (#131) the browser presents on every guarded call and re-confirms the legacy session on
/// boot through <c>GET me</c>. Logout for BOTH audiences is owned by
/// <see cref="SessionLogoutRoutes"/>, which dispatches on the audience the caller presents and
/// revokes this session through <see cref="INodeWebSessionAuthority.LogoutAsync"/>; the bearer path
/// stays accepted for API tooling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allowlist boundary.</b> Only <c>POST /api/session/login</c> bypasses the listener caller-auth
/// gate (you cannot present a session before you have one — same posture as <c>/health</c>).
/// <c>me</c> is GATED — the middleware admits it only WITH a valid legacy session bearer.
/// </para>
/// <para>
/// <b>Non-enumerating.</b> Login returns the SAME 401 for an unknown username, a wrong password, or
/// an un-provisioned credential (<see cref="INodeWebSessionAuthority.LoginAsync"/> returns a typed
/// refusal for all), and for an active lockout. This is a wire body/status guarantee, not full timing
/// equalization: a lockout refusal returns before <see cref="INodeWebSessionAuthority.LoginAsync"/>,
/// so its shorter work remains timing-distinguishable. That accepted residual is documented here;
/// the limiter is a volume cap and its generic 401 prevents a lockout-state oracle in the response.
/// Wiring is closed over the authority resolved from the composition root.
/// </para>
/// </remarks>
public static class WebSessionRoutes
{
    /// <summary>Login — allowlisted (pre-auth). Verifies the password, mints an expiring bearer.</summary>
    public const string LoginPath = "/api/session/login";

    /// <summary>Whoami — gated. Summarizes the presented session.</summary>
    public const string MePath = "/api/session/me";

    /// <summary>Login request body (camelCase wire: <c>{ "username": ..., "password": ... }</c>).</summary>
    public sealed record LoginRequest(string? Username, string? Password);

    private sealed record LoginResponse(string Token, string User, string DisplayName, DateTimeOffset ExpiresAt);

    private sealed record MeResponse(string User, string DisplayName, DateTimeOffset ExpiresAt);

    private sealed record ErrorResponse(string Error);

    /// <summary>
    /// The ONE login-refusal body (non-enumerating): the same 401 for a wrong password, unknown
    /// user, un-provisioned credential, disabled profile, or an active lockout. The stable
    /// <c>Error</c> is the localizable code; no English message is sent on the wire.
    /// </summary>
    private static IResult LoginFailedResponse() =>
        Results.Json(
            new ErrorResponse("login_failed"),
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Maps legacy login and whoami onto <paramref name="app"/>.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        INodeWebSessionAuthority authority,
        WebLoginRateLimiter loginRateLimiter)
    {
        ArgumentNullException.ThrowIfNull(app);

        Map(
            app.MapPreAuthOperationalGroup(),
            app.MapSelectedSessionProductGroup(),
            authority,
            loginRateLimiter);
    }

    internal static void Map(
        IEndpointRouteBuilder preAuth,
        IEndpointRouteBuilder selectedSession,
        INodeWebSessionAuthority authority,
        WebLoginRateLimiter loginRateLimiter)
    {
        ArgumentNullException.ThrowIfNull(preAuth);
        ArgumentNullException.ThrowIfNull(selectedSession);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(loginRateLimiter);

        preAuth.MapPost(LoginPath, (LoginRequest request, HttpContext ctx) =>
            LoginAsync(authority, loginRateLimiter, request, ctx));

        selectedSession.MapGet(MePath, async (HttpContext ctx) =>
        {
            var summary = await authority.DescribeAsync(ctx, ctx.RequestAborted).ConfigureAwait(false);
            return summary is null
                ? Results.Json(
                    new ErrorResponse("no_session"),
                    statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(new MeResponse(summary.User, summary.DisplayName, summary.ExpiresAt));
        });
    }

    internal static async Task<IResult> LoginAsync(
        INodeWebSessionAuthority authority,
        WebLoginRateLimiter loginRateLimiter,
        LoginRequest request,
        HttpContext ctx)
    {
        // S11 rate-limit/lockout, keyed per (username, source address) off the Auth.LoginFailed
        // signal. A refused (locked-out / fail-closed) attempt gets the IDENTICAL 401 body as a
        // wrong password — this is not a wire-level account-existence or lockout-state oracle.
        // An accepted timing residual remains because refusal returns before Argon2id verify.
        // NodeWebSessionAuthority compares the configured founder username with Ordinal, so the
        // legacy route deliberately uses the raw username as its resolution identity. The account
        // challenge route uses the shared normalized account identity instead.
        var resolutionIdentity = request?.Username;
        var source = ctx.Connection.RemoteIpAddress?.MapToIPv6().ToString();
        if (!loginRateLimiter.CheckAttempt(resolutionIdentity, source).IsAllowed)
        {
            return LoginFailedResponse();
        }

        var settled = false;
        try
        {
            var result = await authority
                .LoginAsync(request?.Username, request?.Password, ctx.RequestAborted)
                .ConfigureAwait(false);
            var login = result.Login;
            if (login is null)
            {
                if (result.FailureReason == WebLoginFailureReason.CredentialMismatch)
                {
                    loginRateLimiter.RecordFailure(resolutionIdentity, source);
                    settled = true;
                }

                return LoginFailedResponse();
            }

            loginRateLimiter.RecordSuccess(resolutionIdentity, source);
            settled = true;
            // #131 — mint the browser session cookie (HttpOnly+Secure+SameSite=Strict). A browser
            // rides the cookie thereafter (refresh-surviving); the body token stays for a
            // bearer/tooling caller, which simply ignores the Set-Cookie.
            authority.IssueSessionCookie(ctx, login);
            return Results.Ok(new LoginResponse(
                login.Token,
                login.User,
                login.DisplayName,
                login.ExpiresAt));
        }
        finally
        {
            if (!settled)
            {
                // Cancellation or a transient authority/store fault must not hold the budget for
                // the full sliding window. Completed outcomes already settled their reservation.
                loginRateLimiter.ReleaseAttempt(resolutionIdentity, source);
            }
        }
    }
}
