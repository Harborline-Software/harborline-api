using Microsoft.AspNetCore.Http;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The node's WEB-CLIENT per-user session authority: verifies a roster password login, mints +
/// validates + revokes expiring bearer sessions, and is the accept path the listener caller-auth
/// gate (<see cref="SharedHostedWebApp"/>) consults for a browser user (in ADDITION to the
/// per-boot bootstrap token). Registered only when <c>LocalNode:WebClient:Enabled</c>.
/// </summary>
/// <remarks>
/// This is the local-first-auth (authority = the local roster, password-first) surface for a
/// browser client. It reuses the ADR-0099 session STORE + TTL floors + the ADR-0097 Argon2id
/// password hasher. The browser transport is an <c>HttpOnly</c>+<c>Secure</c>+<c>SameSite=Strict</c>
/// session cookie (#131 — minted by <see cref="IssueSessionCookie"/>, cleared by
/// <see cref="ClearSessionCookie"/>) so a page refresh SURVIVES; the <c>Authorization: Bearer</c>
/// path stays accepted (dual-accept) for bootstrap / API-tooling callers. The cookie is
/// <c>Secure</c>, so it rides the node's HTTPS origin only — the plain-HTTP origin stays bearer-only.
/// </remarks>
public interface INodeWebSessionAuthority
{
    /// <summary>
    /// True when the web-client profile is enabled (the gate consults web sessions; static hosting is
    /// wired). Independent of whether a founder credential is provisioned — an enabled-but-unprovisioned
    /// node serves the login page but fail-closes every login.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// GATE accept path: is this request carrying a valid, unexpired web-session bearer? Looks the
    /// bearer up in the session store, enforces the TTL (absolute + sliding idle), reaps an expired
    /// session, and touches a live one. Returns <c>false</c> for a missing / unknown / expired token
    /// (the gate then falls through to reject 401). NEVER throws.
    /// </summary>
    Task<bool> TryAuthenticateAsync(HttpContext context);

    /// <summary>
    /// Verify a username/password against the founder credential and, on success, mint an expiring
    /// bearer session bound to the node's local operator + active-team tenant. Returns a typed
    /// refusal for malformed input, authority refusal, provisioning refusal, or credential mismatch
    /// — the caller returns the SAME 401 for all so nothing is enumerable. Emits the password-login /
    /// login-failed audit (never logs the password or the token).
    /// </summary>
    Task<WebLoginAttemptResult> LoginAsync(string? username, string? password, CancellationToken ct);

    /// <summary>
    /// Revoke the request's session server-side (logout). Idempotent. Emits the signed-out audit when
    /// a live record was actually removed.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this request PRESENTED a web-session credential (bearer or session cookie),
    /// whether or not a live record was still there to remove — either way the presented credential
    /// now resolves to no live session, which is what sign-out asserts. <c>false</c> ONLY when the
    /// request carried no credential at all, so the caller can refuse rather than claim a sign-out
    /// that never had a subject. Deliberately does NOT distinguish "removed" from "already gone":
    /// that split would make the route a session-id oracle and would break retry-after-failure.
    /// </returns>
    Task<bool> LogoutAsync(HttpContext context, CancellationToken ct);

    /// <summary>
    /// Mint the browser session cookie (#131) onto the response after a successful
    /// <see cref="LoginAsync"/> — <c>HttpOnly</c>+<c>Secure</c>+<c>SameSite=Strict</c>, carrying only
    /// the opaque session id, expiring at the session's absolute expiry (the server-side record is
    /// still the authority). Called by the login route; a no-op difference for a bearer/tooling
    /// caller (it simply ignores the <c>Set-Cookie</c>).
    /// </summary>
    void IssueSessionCookie(HttpContext context, WebLoginResult login);

    /// <summary>
    /// Clear the browser session cookie (#131) on logout — an expired, attribute-matching
    /// <c>Set-Cookie</c> so the browser drops it. Pairs with the server-side revoke in
    /// <see cref="LogoutAsync"/> (that is the security-relevant step; this clears the client copy).
    /// </summary>
    void ClearSessionCookie(HttpContext context);

    /// <summary>
    /// Summarize the request's live session (for whoami) — the bound user + display name + absolute
    /// expiry. Returns <c>null</c> when the bearer has no live session.
    /// </summary>
    Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct);
}

/// <summary>The result of a successful login — the raw bearer token plus its summary.</summary>
/// <param name="Token">
/// The opaque session id (≥256-bit CSPRNG). A browser receives it as the <c>HttpOnly</c> session
/// cookie (never JS-readable); a bearer/tooling caller may read it from the login body.
/// </param>
/// <param name="User">The bound user id (the node's local operator).</param>
/// <param name="DisplayName">A human-friendly label for the session.</param>
/// <param name="ExpiresAt">Server-enforced absolute expiry (the session dies here regardless of activity).</param>
public sealed record WebLoginResult(string Token, string User, string DisplayName, DateTimeOffset ExpiresAt);

/// <summary>The outcome of one legacy web-session login attempt.</summary>
public sealed record WebLoginAttemptResult(WebLoginResult? Login, WebLoginFailureReason? FailureReason);

/// <summary>The whoami summary of a live session (never carries the raw token).</summary>
/// <param name="User">The bound user id.</param>
/// <param name="DisplayName">A human-friendly label.</param>
/// <param name="ExpiresAt">Server-enforced absolute expiry.</param>
public sealed record WebSessionSummary(string User, string DisplayName, DateTimeOffset ExpiresAt);
