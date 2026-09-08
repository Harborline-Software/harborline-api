using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// inc-4 cross-process CALLER AUTH — the per-boot session-token guard for the
/// loopback node routes the Harborline App calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes (the load-bearing security statement).</b> The shared
/// Kestrel listener binds <c>127.0.0.1</c> only, but a loopback bind authenticates
/// the HOST, not the calling PROCESS: any local process — a malicious sibling app, a
/// curious script — can reach a loopback port and read/write the node. The
/// <see cref="CurrentPrincipalSignatureRoutes"/> INC-4 TODO names this explicitly. So
/// loopback alone is NOT sufficient caller-auth, and per
/// <c>project_identity_acquisition_modes</c> (loopback ≠ trusted) the connection must
/// authenticate the caller, not just the box.
/// </para>
/// <para>
/// <b>The gate is LISTENER-LEVEL (inc-4 F1 — gate-all-by-default).</b> This guard is
/// applied by the <c>SharedHostedWebApp</c> caller-auth MIDDLEWARE, which requires the
/// bearer for EVERY route on the loopback listener by default, with a tiny explicit
/// allowlist (<c>/health</c>, <c>/live</c>, <c>/ready</c> probes + <c>/ws</c> peer-sync — each authenticated by
/// its own layer). inc-4 (#1286) originally applied this guard per-route (opt-in on 3
/// route groups only), which left the UNAUTHENTICATED financial-cluster write routes
/// reachable — the forgotten-route problem. F1 inverts the default: a route is gated
/// unless explicitly allowlisted, so a newly-added route is secure by construction and
/// the financial write path is now covered. The 3 original route groups retain their
/// per-route checks as defence-in-depth; the middleware is the authoritative gate.
/// </para>
/// <para>
/// <b>The mechanism (a bounded per-boot shared secret — NOT a full key-exchange).</b>
/// At boot the Tauri shell that spawns this node generates a fresh 256-bit CSPRNG
/// token, injects it via the <c>LocalNode__SessionToken</c> env var (
/// <see cref="LocalNodeOptions.SessionToken"/>) AND holds it in the shell. The Harborline App
/// presents it on every guarded call as <c>Authorization: Bearer &lt;token&gt;</c>;
/// this guard <see cref="Validate"/>s it. The token is per-boot, never persisted on
/// either side, and dies with the process pair. A deeper key-exchange / mutual-attest
/// substrate (signed handshake, OS-peer-credential check) is FLAGGED follow-up — the
/// per-boot shared secret is the bounded inc-4 deliverable that NEVER ships an
/// unauthenticated loopback.
/// </para>
/// <para>
/// <b>Fail-closed (the invariant the test pins).</b> When a token is configured, a
/// missing OR mismatched bearer is REJECTED with 401 — a local stranger is turned
/// away. The compare is constant-time (<see cref="Matches"/>) to deny a timing
/// side-channel. When NO token is configured (a direct <c>dotnet run</c> / a
/// Bridge-spawned tenant child) the guard is in dev/single-host-trusted mode and
/// permits the call — the Harborline App ALWAYS injects a token, so the production Harborline App
/// path is always authenticated; the null path is the dev/test/Bridge fallback the
/// node logs loudly at startup.
/// </para>
/// </remarks>
public sealed class NodeCallerSessionToken
{
    /// <summary>The HTTP header the caller presents the bearer token in.</summary>
    public const string HeaderName = "Authorization";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key the AUTHORITATIVE listener-level gate
    /// (<c>SharedHostedWebApp</c>'s caller-auth middleware) stamps when it ACCEPTS a request — on ANY
    /// of its accept paths: the allowlist, the bootstrap token (Accept 1), a web-client per-user
    /// session (Accept 2, #1842), or the un-enforced dev fast-path. The per-route defence-in-depth
    /// <see cref="Validate"/> reads it and trusts a request the authoritative gate already
    /// authenticated.
    /// </summary>
    /// <remarks>
    /// This closes the gap that bounced the #1842 web-client login: the listener gate accepts a
    /// web-client session (Accept 2), but a per-route check only knows THIS bootstrap token, so it
    /// falsely returned 401 for a valid web session that the gate had already let through — and the
    /// browser bounced back to the login screen on that 401. The marker is <b>server-only per-request
    /// state</b>: it is set exclusively by trusted middleware, never read from the request, so a
    /// client cannot forge it; and a request that does NOT pass the gate is rejected by the gate and
    /// never reaches a route handler, so the per-route check's fail-closed behaviour for a genuinely
    /// unauthenticated caller is unchanged. A route mapped WITHOUT the listener gate (a unit test / a
    /// misconfiguration) sets no marker, so <see cref="Validate"/> falls back to independent
    /// bootstrap-token validation exactly as before — defence-in-depth is preserved.
    /// </remarks>
    public const string GatePassedItemKey = "inc4:caller-auth:gate-passed";

    /// <summary>The bearer scheme prefix (case-insensitive per RFC 7235).</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The configured token bytes (UTF-8), or <see langword="null"/> when no token was
    /// injected (dev/single-host-trusted mode). Kept as raw bytes for a constant-time
    /// compare that does not allocate a comparison string per request.
    /// </summary>
    private readonly byte[]? _expected;

    /// <summary>
    /// Builds the guard from the configured token. A null/blank token ⇒
    /// <see cref="IsEnforced"/> false (dev/single-host-trusted). A configured token ⇒
    /// the guard enforces a matching bearer fail-closed.
    /// </summary>
    /// <param name="configuredToken">
    /// The per-boot session token from <see cref="LocalNodeOptions.SessionToken"/>
    /// (typically <c>LocalNode__SessionToken</c>). Null/blank in dev/Bridge-tenant runs.
    /// </param>
    public NodeCallerSessionToken(string? configuredToken)
    {
        _expected = string.IsNullOrWhiteSpace(configuredToken)
            ? null
            : Encoding.UTF8.GetBytes(configuredToken);
    }

    /// <summary>
    /// <see langword="true"/> when a token is configured and the guard is therefore
    /// ENFORCING caller-auth (the production Harborline App posture). <see langword="false"/>
    /// in dev/single-host-trusted mode (no token injected).
    /// </summary>
    public bool IsEnforced => _expected is not null;

    /// <summary>
    /// Constant-time compare of a presented token against the configured one. Returns
    /// <see langword="false"/> for any length mismatch or byte difference WITHOUT an
    /// early-out that would leak the matched-prefix length via timing.
    /// </summary>
    /// <remarks>
    /// Only callable meaningfully when <see cref="IsEnforced"/> — a guard with no
    /// configured token has nothing to match and returns <see langword="false"/> here
    /// (the <see cref="Validate"/> path short-circuits to allow BEFORE reaching this in
    /// the un-enforced case).
    /// </remarks>
    public bool Matches(string? presentedToken)
    {
        if (_expected is null || presentedToken is null)
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(presentedToken);
        // CryptographicOperations.FixedTimeEquals is length-safe + constant-time: it
        // does NOT early-out on a length mismatch, so neither the length nor the
        // matched-prefix of the secret leaks through response timing.
        return CryptographicOperations.FixedTimeEquals(presented, _expected);
    }

    /// <summary>
    /// The verdict of validating a request's caller-auth.
    /// </summary>
    public enum Decision
    {
        /// <summary>The call may proceed (valid bearer, or dev/un-enforced mode).</summary>
        Allow,

        /// <summary>The call is rejected fail-closed — no/invalid bearer while enforced (401).</summary>
        Reject,
    }

    /// <summary>
    /// Validate the caller-auth on an incoming request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Enforced mode</b> (a token is configured): extract the <c>Authorization:
    /// Bearer &lt;token&gt;</c> header and constant-time compare it; a missing header, a
    /// non-Bearer scheme, or a mismatched token all ⇒ <see cref="Decision.Reject"/>.
    /// </para>
    /// <para>
    /// <b>Un-enforced mode</b> (no token configured): <see cref="Decision.Allow"/>
    /// immediately — the dev/single-host-trusted fallback. The header is not even read.
    /// </para>
    /// </remarks>
    public Decision Validate(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The AUTHORITATIVE listener-level gate already accepted this request (bootstrap token, a
        // web-client session, or the allowlist) — the per-route defence-in-depth check trusts that
        // decision. Without this, a valid web-client session (accepted by the gate's Accept-2 path,
        // #1842) is falsely rejected here because this per-route check knows only the bootstrap
        // token, which bounced the browser back to the login screen (the #1842 sync-status 401).
        // The marker is server-only per-request state set by trusted middleware — never read from
        // the request, so it cannot be forged; a request that did not pass the gate never reaches a
        // route handler.
        if (request.HttpContext?.Items.TryGetValue(GatePassedItemKey, out var gatePassed) == true &&
            gatePassed is true)
        {
            return Decision.Allow;
        }

        // Dev / single-host-trusted: no token to enforce. The shipped Harborline App ALWAYS
        // injects a token, so this branch is the dev/test/Bridge-tenant path only.
        if (!IsEnforced)
        {
            return Decision.Allow;
        }

        if (!request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Decision.Reject; // No Authorization header at all → fail closed.
        }

        var header = values.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Decision.Reject; // Wrong/absent scheme → fail closed.
        }

        var presented = header.Substring(BearerPrefix.Length).Trim();
        return Matches(presented) ? Decision.Allow : Decision.Reject;
    }

    /// <summary>
    /// Stamp <paramref name="context"/> as having passed the AUTHORITATIVE listener-level caller-auth
    /// gate. Called by <c>SharedHostedWebApp</c>'s middleware immediately before it lets a request
    /// proceed on ANY accept path (allowlist / bootstrap token / web-client session / un-enforced),
    /// so a per-route defence-in-depth <see cref="Validate"/> downstream can recognize a request the
    /// gate already authenticated on a path this bootstrap-token guard cannot see on its own (the
    /// #1842 web-client session). See <see cref="GatePassedItemKey"/>.
    /// </summary>
    public static void MarkGatePassed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[GatePassedItemKey] = true;
    }

    /// <summary>
    /// Enforce caller-auth on a request, writing a 401 response when rejected. Returns
    /// <see langword="true"/> when the route handler may proceed, <see langword="false"/>
    /// when the request was rejected (the caller should return an <c>IResult</c> built
    /// from the same status — see <see cref="RejectResult"/>).
    /// </summary>
    /// <remarks>
    /// Convenience for minimal-API handlers: pattern is
    /// <c>if (token.Validate(ctx.Request) is Decision.Reject) return NodeCallerSessionToken.RejectResult();</c>.
    /// </remarks>
    public static Microsoft.AspNetCore.Http.IResult RejectResult() =>
        Results.Json(
            new CallerAuthError("caller_unauthenticated",
                "This local-node route requires the Harborline app session token (Authorization: Bearer). " +
                "A loopback bind authenticates the host, not the calling process."),
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>The 401 body shape (camelCase wire by default JsonSerializerOptions).</summary>
    public sealed record CallerAuthError(string Error, string Message);
}
