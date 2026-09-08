using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Session;

/// <summary>
/// The v1 production-and-test <see cref="ISessionEstablisher"/> (ADR 0099 H7). The ONLY code
/// path that calls <c>SignInAsync</c>. Implements the S1 fixation mechanism, the no-pre-auth-
/// record invariant, the cookie-carries-only-the-opaque-id posture (A6), and the reason-
/// specific audit emission, against an injected <see cref="ISessionStore"/>.
/// </summary>
/// <remarks>
/// The actual <c>Auth.SessionEstablished.*</c> audit append is performed by the Bridge
/// integration (PR-3/PR-4) which owns the audit-log surface; this substrate logs the
/// establishment at information level with the canonical event-type string
/// (<see cref="AuditEventTypes"/>) so the establishment is observable even before the Bridge
/// audit wiring lands, and so the single-seam invariant (H7) is enforced in the substrate
/// rather than per-call-site. The audit-log emission is added when the establisher is wired
/// to the Bridge audit sink (it has no <c>foundation</c>-audit dependency here to keep the
/// acyclic O-7 layering — the package references only <c>foundation</c> primitives + the
/// ASP.NET Core shared framework).
/// </remarks>
public sealed class SessionEstablisher : ISessionEstablisher
{
    private readonly ISessionStore _store;
    private readonly SessionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionEstablisher> _logger;

    /// <summary>Creates the establisher.</summary>
    /// <param name="store">The server-side session store.</param>
    /// <param name="options">TTL + entropy options (already validated at registration).</param>
    /// <param name="timeProvider">
    /// Clock source — injected so tests pin <c>IssuedUtc</c>/expiry deterministically. Defaults
    /// to <see cref="TimeProvider.System"/> in production via DI.
    /// </param>
    /// <param name="logger">Logger.</param>
    public SessionEstablisher(
        ISessionStore store,
        IOptions<SessionOptions> options,
        TimeProvider timeProvider,
        ILogger<SessionEstablisher> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<SessionEstablishmentResult> EstablishAsync(
        SessionEstablishmentRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpContext);
        cancellationToken.ThrowIfCancellationRequested();

        // --- Sentinel + principal guards (S8 / ADR 0084) ---------------------------------
        // A session MUST NOT bind to a system sentinel tenant (default / __system__ /
        // __-prefixed) or an empty user. Reject at the typed boundary, fail-closed.
        if (request.TenantId.IsSystemSentinel)
        {
            throw new ArgumentException(
                "A session may not be bound to a system sentinel tenant "
                + "(default / __system__ / '__'-prefixed) — ADR 0099 S8 / ADR 0084.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            throw new ArgumentException(
                "A session may not be established for an empty user id — ADR 0099 H1.",
                nameof(request));
        }

        // --- S1 fixation: invalidate any inbound session, never derive from it ------------
        // If this request already carries an authenticated session (e.g. an anonymous→auth
        // privilege change still riding an old ticket), revoke that server-side record and
        // sign the old ticket out BEFORE minting the new one. The new id is freshly minted
        // from the CSPRNG and is NEVER derived from the inbound id (C1). There is no pre-auth
        // SessionRecord to "upgrade" (C2) — EstablishAsync creates the FIRST record for the
        // authenticated principal.
        var inboundSessionId = httpContext.User?.FindFirst(SessionClaimTypes.SessionId)?.Value;
        if (!string.IsNullOrEmpty(inboundSessionId))
        {
            await _store.RemoveAsync(inboundSessionId, cancellationToken).ConfigureAwait(false);
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                .ConfigureAwait(false);
        }

        // --- Mint the fresh session (S4 CSPRNG opaque id) ---------------------------------
        var now = _timeProvider.GetUtcNow();
        var sessionId = SessionIdGenerator.Generate(_options.SessionIdByteLength);

        var record = new SessionRecord
        {
            SessionId = sessionId,
            UserId = request.UserId,
            TenantId = request.TenantId,
            IssuedUtc = now,
            AbsoluteExpiryUtc = now + _options.AbsoluteLifetime,
            LastSeenUtc = now,
            Reason = request.Reason,
        };

        await _store.CreateAsync(record, cancellationToken).ConfigureAwait(false);

        // --- Issue the cookie via SignInAsync — MINIMAL opaque-id-only principal (A6) ------
        // Only the 'sid' claim is serialized into the cookie. sub/tid/roles are NEVER in the
        // cookie; OnValidatePrincipal rehydrates them from the server-side record on each
        // request. This is the single SignInAsync call site (H7).
        var principal = BuildOpaqueIdPrincipal(sessionId);
        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
            .ConfigureAwait(false);

        var auditEventType = AuditEventTypes.SessionEstablishedFor(request.Reason);
        _logger.LogInformation(
            "Session established ({AuditEventType}) for tenant {TenantId}, reason {Reason}.",
            auditEventType, record.TenantId.Value, request.Reason);

        return new SessionEstablishmentResult(sessionId, record.IssuedUtc, record.AbsoluteExpiryUtc);
    }

    /// <summary>
    /// Builds the minimal cookie principal carrying ONLY the opaque session id (A6) — no
    /// <c>sub</c>/<c>tid</c>/roles. <c>OnValidatePrincipal</c> replaces this with a rehydrated
    /// principal read from the server-side record.
    /// </summary>
    private static ClaimsPrincipal BuildOpaqueIdPrincipal(string sessionId)
    {
        var identity = new ClaimsIdentity(
            new List<Claim> { new(SessionClaimTypes.SessionId, sessionId) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}
