using System.Buffers.Text;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Session;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The default <see cref="INodeWebSessionAuthority"/> — the node's per-user WEB-CLIENT login +
/// bearer-session authority. Verifies the founder password (ADR 0097 Argon2id), mints EXPIRING
/// bearer sessions into the reused ADR-0099 <see cref="ISessionStore"/> with the ADR-0099 TTL
/// floors, validates/touches/revokes them, and is the gate accept path for a browser user.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reused, not reinvented (search-before-substrate).</b> The crypto is the ADR-0097
/// <c>Argon2idPasswordHasher</c>; the session record, store, TTL floors (8h absolute / 30min idle),
/// and CSPRNG id entropy (≥256-bit) are the ADR-0099 substrate. The server-side session semantics
/// are IDENTICAL to the bearer era — the SAME <see cref="ISessionStore"/> record, the SAME 8h
/// absolute / 30min sliding-idle enforcement (<see cref="TryAuthenticateAsync"/> reaps + touches),
/// and logout revokes the record server-side. Only the browser TRANSPORT changed.
/// </para>
/// <para>
/// <b>Transport (#131): an HttpOnly+Secure+SameSite=Strict cookie for browsers, a bearer for
/// tooling.</b> A browser user's session now rides the ADR-0099-shaped session cookie
/// (<see cref="SessionCookieName"/>) minted at login by <see cref="IssueSessionCookie"/>: it carries
/// ONLY the opaque session id, is <c>HttpOnly</c> (JS cannot read it — an XSS cannot exfiltrate the
/// session, the one real downside the prior in-memory bearer could not close), <c>Secure</c> (sent
/// only over HTTPS — the node's <c>https://…:8891</c> Caddy/Kestrel origin; the plain-HTTP
/// <c>:8890</c> origin therefore does not carry it and stays bearer-only for tooling), and
/// <c>SameSite=Strict</c> (a cross-site page cannot make the browser attach it — the CSRF defense,
/// paired with the same-origin bundle). The <c>__Host-</c> name prefix pins it host-only + Path=/ +
/// Secure. The <c>Authorization: Bearer</c> path is UNCHANGED and still accepted (dual-accept) so
/// bootstrap / API-tooling callers (curl) keep working; <see cref="ExtractSessionToken"/> reads a
/// bearer first, then the cookie. Because a reload re-presents the cookie, the browser session now
/// SURVIVES a refresh (the gate re-confirms it via an authenticated <c>GET /api/session/me</c>).
/// </para>
/// <para>
/// <b>Fail-closed + non-enumerating.</b> An un-provisioned credential, an unknown username, or a
/// wrong password all return typed refusals from <see cref="LoginAsync"/> — the route returns the
/// SAME 401 for every case. The Argon2id verify runs on EVERY credential-shaped attempt (even an
/// unknown username) so the response timing does not reveal whether the username existed. The
/// password and the minted token are NEVER logged (only the login outcome + the canonical audit event
/// type).
/// </para>
/// </remarks>
public sealed class NodeWebSessionAuthority : INodeWebSessionAuthority
{
    // Canonical Auth.* audit event-type strings (ADR 0099 §S10 — mirrored here; the node is not the
    // Bridge audit sink, so — exactly as the ADR-0099 SessionEstablisher documents for the pre-sink
    // state — the node emits these via structured logging with the canonical labels. Durable
    // hash-chained auth-audit rows (NodeAuditEventRow) are a documented follow-up; the node's audit
    // chain is financial-only today).
    private const string AuditPasswordLogin = "Auth.SessionEstablished.PasswordLogin";
    private const string AuditLoginFailed = "Auth.LoginFailed";
    private const string AuditSignedOut = "Auth.SignedOut";

    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The web-client session cookie name (#131). The <c>__Host-</c> prefix is a browser-enforced
    /// security contract: the cookie is accepted ONLY when it is <c>Secure</c>, has <c>Path=/</c>,
    /// and carries NO <c>Domain</c> (host-only) — so a network/sibling-origin attacker cannot set or
    /// overwrite it, and it is never sent over plain HTTP. It carries only the opaque session id
    /// (ADR-0099 A6: everything else lives in the server-side <see cref="SessionRecord"/>).
    /// </summary>
    public const string SessionCookieName = "__Host-web_session";

    // Defense-in-depth input caps (the Argon2 substrate independently caps at 4096; these bound the
    // route-tier surface so an absurd body cannot be handed to the hasher).
    private const int MaxUsernameLength = 256;
    private const int MaxPasswordLength = 4096;

    private readonly NodeWebClientOptions _options;
    private readonly IPasswordHasher<NodeWebUser> _hasher;
    private readonly ISessionStore _store;
    private readonly SessionOptions _sessionOptions;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IInstallationIdentityV1AuthorityGate _v1AuthorityGate;
    private readonly TimeProvider _time;
    private readonly ILogger<NodeWebSessionAuthority> _logger;

    /// <summary>Constructs the authority from the reused ADR-0097 hasher + ADR-0099 session store/options.</summary>
    public NodeWebSessionAuthority(
        IOptions<NodeWebClientOptions> options,
        IPasswordHasher<NodeWebUser> hasher,
        ISessionStore store,
        IOptions<SessionOptions> sessionOptions,
        IActiveTeamAccessor activeTeam,
        IInstallationIdentityV1AuthorityGate v1AuthorityGate,
        TimeProvider time,
        ILogger<NodeWebSessionAuthority> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessionOptions);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(v1AuthorityGate);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _hasher = hasher;
        _store = store;
        _sessionOptions = sessionOptions.Value;
        _activeTeam = activeTeam;
        _v1AuthorityGate = v1AuthorityGate;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Enabled;

    /// <summary>True only when a founder credential is fully provisioned (else login is fail-closed).</summary>
    private bool HasCredential =>
        !string.IsNullOrWhiteSpace(_options.FounderUsername) &&
        !string.IsNullOrWhiteSpace(_options.FounderPasswordHash);

    /// <inheritdoc />
    public async Task<bool> TryAuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var token = ExtractSessionToken(context.Request);
        if (token is null)
        {
            return false;
        }
        var audience = ExtractBearer(context.Request) is null
            ? InstallationIdentityLegacyBearerAudience.SelectedUser
            : InstallationIdentityLegacyBearerAudience.Tooling;
        var admission = await _v1AuthorityGate.CheckLegacyBearerAdmissionAsync(
                audience,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (!admission.IsAllowed)
        {
            return false;
        }

        // The session id IS the bearer (ADR 0099: store lookups are by-key on the high-entropy id;
        // no byte compare needed — a 256-bit CSPRNG id is not brute-forceable by-key).
        var record = await _store.GetAsync(token, context.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        if (record.IsExpired(now, _sessionOptions.IdleTimeout))
        {
            // Reap the expired record so a stale bearer dies immediately.
            await _store.RemoveAsync(token, context.RequestAborted).ConfigureAwait(false);
            return false;
        }

        // Slide the idle window.
        await _store.TouchAsync(token, now, context.RequestAborted).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<WebLoginAttemptResult> LoginAsync(string? username, string? password, CancellationToken ct)
    {
        // Bound the inputs before touching the hasher (defense-in-depth).
        if (string.IsNullOrWhiteSpace(username) || password is null ||
            username.Length > MaxUsernameLength || password.Length > MaxPasswordLength)
        {
            LogLoginFailed(username, "malformed or oversized credentials");
            return new(null, WebLoginFailureReason.MalformedInput);
        }
        var admission = await _v1AuthorityGate.CheckV1MutationAdmissionAsync(ct)
            .ConfigureAwait(false);
        if (!admission.IsAllowed)
        {
            LogLoginFailed(username, admission.RefusalCode ?? "legacy v1 mutation refused");
            return new(null, WebLoginFailureReason.AuthorityRefused);
        }

        if (!_options.Enabled || !HasCredential)
        {
            LogLoginFailed(username, _options.Enabled ? "no founder credential provisioned" : "web-client disabled");
            return new(null, WebLoginFailureReason.ProvisioningRefused);
        }

        // Run the Argon2id verify on EVERY attempt (even a username miss) so timing does not reveal
        // whether the username existed. FounderPasswordHash is non-null here (HasCredential checked).
        var verify = _hasher.VerifyHashedPassword(NodeWebUser.Instance, _options.FounderPasswordHash!, password);
        var usernameMatches = string.Equals(username, _options.FounderUsername, StringComparison.Ordinal);
        var passwordOk = verify != PasswordVerificationResult.Failed;

        if (!usernameMatches || !passwordOk)
        {
            LogLoginFailed(username, "credential mismatch");
            return new(null, WebLoginFailureReason.CredentialMismatch);
        }

        // Mint the expiring bearer session bound to the local operator + the active-team tenant.
        var tenant = NodeTenant.Resolve(_activeTeam);
        var now = _time.GetUtcNow();
        var sessionId = GenerateSessionId(_sessionOptions.SessionIdByteLength);
        var record = new SessionRecord
        {
            SessionId = sessionId,
            UserId = ActiveTeamAuthorizationContext.LocalUserId,
            TenantId = tenant,
            IssuedUtc = now,
            AbsoluteExpiryUtc = now + _sessionOptions.AbsoluteLifetime,
            LastSeenUtc = now,
            Reason = SessionEstablishmentReason.PasswordLogin,
        };
        await _store.CreateAsync(record, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "{AuditEventType}: web-client login succeeded for user {User} (tenant {Tenant}); session expires {ExpiresAt:o}.",
            AuditPasswordLogin, ActiveTeamAuthorizationContext.LocalUserId, tenant.Value, record.AbsoluteExpiryUtc);

        var displayName = string.IsNullOrWhiteSpace(_options.FounderDisplayName)
            ? _options.FounderUsername!
            : _options.FounderDisplayName!;
        return new(
            new WebLoginResult(
                sessionId,
                ActiveTeamAuthorizationContext.LocalUserId,
                displayName,
                record.AbsoluteExpiryUtc),
            null);
    }

    /// <inheritdoc />
    public async Task<bool> LogoutAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Revoke EVERY credential this request presents — not just the first one the gate would
        // accept. ExtractSessionToken is bearer-FIRST, so resolving through it would let a stale or
        // junk bearer SHADOW a live session cookie: the store remove would miss, this method would
        // still report a credential was presented, and the route would answer 204 and expire the
        // cookie while the cookie's session stayed live and usable via its own transport. Sign-out
        // has to be true of every credential the caller handed us, or the 204 is a false claim.
        var bearer = ExtractBearer(context.Request);
        var cookie = ExtractCookieToken(context.Request);
        var tokens = new List<string>(capacity: 2);
        if (bearer is not null)
        {
            tokens.Add(bearer);
        }
        // Both transports carry the SAME by-key store id, so a caller presenting one value twice is
        // one credential, not two. De-duplicating avoids issuing a redundant second store remove for
        // that key, which would find nothing and return false. It is an EFFICIENCY guard, not a
        // correctness one: the loop below accumulates with |=, so a false second remove could not
        // have cleared a true first one, and the audit line is emitted once outside the loop
        // regardless of how many tokens were tried.
        if (cookie is not null && !string.Equals(cookie, bearer, StringComparison.Ordinal))
        {
            tokens.Add(cookie);
        }
        if (tokens.Count == 0)
        {
            return false;
        }

        // NOT gated by the cutover authority, and that asymmetry with the other three v1 entry
        // points is the whole point. TryAuthenticateAsync / DescribeAsync / LoginAsync all GRANT
        // something, so the gate refuses them the moment the barrier rises. Revocation grants
        // nothing: it only destroys a legacy credential, which is precisely what the cutover exists
        // to accomplish (ADR 0160 R3-H step 6 stages "revoke every legacy bearer by audience" as a
        // PRECONDITION of the marker CAS). Refusing to revoke during the cutover would invert the
        // gate — it would PRESERVE live v1 records for exactly the stages that are supposed to be
        // disposing of them.
        //
        // Do not reinstate the earlier ordering, which consulted the gate here and returned false on
        // refusal. It left every presented record LIVE while SessionLogoutRoutes.SelectedLogoutAsync
        // answered 204 anyway (that branch revokes the selected session first and ignores this
        // method's answer), so the response claimed a sign-out that had not happened and the record
        // stayed capable of resurrection if the gate or the stage were restored or bypassed.
        var revoked = false;
        foreach (var token in tokens)
        {
            revoked |= await _store.RemoveAsync(token, ct).ConfigureAwait(false);
        }
        if (revoked)
        {
            _logger.LogInformation("{AuditEventType}: web-client session revoked (logout).", AuditSignedOut);
        }
        // True whether or not a live record was found: a credential was presented, and every
        // credential presented now resolves to no live session. Reporting "already gone" as a
        // failure would refuse every retry after a partial sign-out and would leak whether a given
        // session id existed.
        return true;
    }

    /// <inheritdoc />
    public async Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = ExtractSessionToken(context.Request);
        if (token is null)
        {
            return null;
        }
        var audience = ExtractBearer(context.Request) is null
            ? InstallationIdentityLegacyBearerAudience.SelectedUser
            : InstallationIdentityLegacyBearerAudience.Tooling;
        var admission = await _v1AuthorityGate.CheckLegacyBearerAdmissionAsync(audience, ct)
            .ConfigureAwait(false);
        if (!admission.IsAllowed)
        {
            return null;
        }
        var record = await _store.GetAsync(token, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }
        var displayName = string.IsNullOrWhiteSpace(_options.FounderDisplayName)
            ? (_options.FounderUsername ?? record.UserId)
            : _options.FounderDisplayName!;
        return new WebSessionSummary(record.UserId, displayName, record.AbsoluteExpiryUtc);
    }

    /// <summary>
    /// Emit the login-failed audit (the S11 rate-limit/lockout signal). Logs the OUTCOME + reason +
    /// (best-effort) the attempted username — NEVER the password.
    /// </summary>
    private void LogLoginFailed(string? username, string reason)
    {
        _logger.LogWarning(
            "{AuditEventType}: web-client login rejected ({Reason}); attempted-user={User}.",
            AuditLoginFailed, reason, string.IsNullOrEmpty(username) ? "(none)" : username);
    }

    /// <inheritdoc />
    public void IssueSessionCookie(HttpContext context, WebLoginResult login)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(login);

        // Persistent up to the session's ABSOLUTE expiry (so a reload/browser-restart within the 8h
        // cap re-presents it); the server still enforces BOTH the absolute cap and the 30min sliding
        // idle window on every request (TryAuthenticateAsync), so the cookie lifetime is an upper
        // bound, never the authority (ADR 0099 B/D1).
        context.Response.Cookies.Append(SessionCookieName, login.Token, BuildCookieOptions(login.ExpiresAt));
    }

    /// <inheritdoc />
    public void ClearSessionCookie(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Emit a matching-attribute expired Set-Cookie so the browser drops it. The attributes MUST
        // match the mint (Secure / Path=/ / SameSite) or the browser will not match+delete a
        // __Host- cookie. The server-side record is revoked separately in LogoutAsync.
        context.Response.Cookies.Delete(SessionCookieName, BuildCookieOptions(expires: null));
    }

    /// <summary>
    /// The security attributes for the session cookie (#131). <c>HttpOnly</c> (no JS read →
    /// XSS-safe), <c>Secure</c> (HTTPS only), <c>SameSite=Strict</c> (CSRF defense — never sent
    /// cross-site), <c>Path=/</c> + no <c>Domain</c> (host-only; required by the <c>__Host-</c>
    /// prefix), <c>IsEssential</c> (an auth cookie is not subject to consent gating).
    /// </summary>
    private static CookieOptions BuildCookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        Expires = expires,
    };

    /// <summary>
    /// The gate/read accept path: the opaque session id presented by this request, from EITHER an
    /// <c>Authorization: Bearer</c> header (bootstrap / API-tooling — tried first, unchanged) OR the
    /// web-client <see cref="SessionCookieName"/> cookie (browser). Null when neither is present.
    /// Both carry the same by-key store id, so a caller presents at most one in practice.
    /// </summary>
    private static string? ExtractSessionToken(HttpRequest request)
    {
        var bearer = ExtractBearer(request);
        return bearer ?? ExtractCookieToken(request);
    }

    /// <summary>
    /// The opaque session id carried by the web-client session COOKIE alone, or null when absent.
    /// Split out from <see cref="ExtractSessionToken"/> because logout must reason about each
    /// transport separately (a bearer must not shadow a live cookie), while the read/gate paths
    /// legitimately want the single credential the gate would accept.
    /// </summary>
    private static string? ExtractCookieToken(HttpRequest request) =>
        request.Cookies.TryGetValue(SessionCookieName, out var cookie) && !string.IsNullOrEmpty(cookie)
            ? cookie
            : null;

    /// <summary>Extract the <c>Authorization: Bearer &lt;token&gt;</c> value, or null when absent/malformed.</summary>
    private static string? ExtractBearer(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values))
        {
            return null;
        }
        var header = values.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var token = header[BearerPrefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// Mint an opaque CSPRNG session id (mirrors ADR 0099 <c>SessionIdGenerator</c>, which is internal):
    /// N random bytes, base64url-encoded (URL-safe, unpadded). Hard-floors the length at the ADR 0099
    /// substrate minimum (16 bytes / 128-bit); the configured default is 32 (256-bit).
    /// </summary>
    private static string GenerateSessionId(int byteLength)
    {
        if (byteLength < SessionOptions.MinimumSessionIdByteLength)
        {
            byteLength = SessionOptions.MinimumSessionIdByteLength;
        }
        Span<byte> buffer = stackalloc byte[byteLength];
        RandomNumberGenerator.Fill(buffer);
        return Base64Url.EncodeToString(buffer);
    }
}
