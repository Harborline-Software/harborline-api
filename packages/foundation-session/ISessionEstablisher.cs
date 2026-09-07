using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Session;

/// <summary>
/// The SINGLE session-create seam (ADR 0099 H7). All three auth entry
/// points — password login, verify-email completion, magic-link consume —
/// route through <see cref="EstablishAsync"/>. No other code path calls
/// <c>SignInAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Signature PINNED by the .NET-architect council (ADR 0099 O-6 / A5).</b> Three
/// sharpenings vs the Rev-1 sketch: parameters bundled into
/// <see cref="SessionEstablishmentRequest"/> (so adding e.g. an MFA field does not break
/// the three call sites); strong-typed <see cref="TenantId"/> (sentinel-reject at the typed
/// boundary, not a string compare inside); and <see cref="HttpContext"/> stays a SEPARATE
/// parameter (the request record is serializable/testable independent of the web pipeline,
/// while <c>SignInAsync</c> genuinely needs the live context). Keeping
/// <see cref="HttpContext"/> separate is also what lets this package reference only the
/// ASP.NET Core shared framework + <c>foundation</c>, NOT <c>foundation-authorization</c>
/// (ADR 0099 O-7 / A8 acyclic layering).
/// </para>
/// <para>
/// <b>Behavior (ADR 0099 §D2):</b> regenerate the session id (S1 fixation; fresh CSPRNG id,
/// never derived from any inbound/pre-auth id, no pre-auth <see cref="SessionRecord"/>) →
/// write a <see cref="SessionRecord"/> to <see cref="ISessionStore"/> → issue the
/// HttpOnly+Secure+SameSite cookie via <c>SignInAsync</c> with a minimal opaque-id-only
/// principal (A6; the cookie carries ONLY the session id, never <c>sub</c>/<c>tid</c>/roles)
/// → emit the reason-specific <c>Auth.SessionEstablished.*</c> audit.
/// </para>
/// </remarks>
public interface ISessionEstablisher
{
    /// <summary>
    /// Regenerates the session id (S1 fixation), writes a <see cref="SessionRecord"/>
    /// to <see cref="ISessionStore"/>, issues the HttpOnly+Secure+SameSite cookie via
    /// <c>SignInAsync</c> (minimal opaque-id-only principal; ADR 0099 D1/A6), and emits the
    /// reason-specific <c>Auth.SessionEstablished.*</c> audit.
    /// </summary>
    /// <param name="request">
    /// The user id, bound tenant (strong-typed; MUST NOT be a system sentinel), and
    /// establishment reason. See <see cref="SessionEstablishmentRequest"/>.
    /// </param>
    /// <param name="httpContext">
    /// The live request context. <c>SignInAsync</c> writes the cookie onto it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The established session's record summary. The caller uses
    /// <see cref="SessionEstablishmentResult.SessionId"/> for audit correlation; the raw
    /// cookie is never returned — <c>SignInAsync</c> owns the cookie write.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="SessionEstablishmentRequest.TenantId"/> is a system sentinel
    /// (<c>default</c> / <c>__system__</c> / any <c>__</c>-prefixed value; ADR 0084 /
    /// <see cref="TenantId.IsSystemSentinel"/>) or <see cref="SessionEstablishmentRequest.UserId"/>
    /// is null/empty — a session MUST NOT bind to a sentinel tenant or an empty principal.
    /// </exception>
    ValueTask<SessionEstablishmentResult> EstablishAsync(
        SessionEstablishmentRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken);
}

/// <summary>
/// The inputs to a session establishment. Bundled into a record so the three call sites
/// (login / verify-email / magic-link) and future MFA-step extensibility (ADR 0099 O-S5)
/// can add a field without breaking call-site signatures. Serializable/testable
/// independent of the web pipeline (the live <see cref="HttpContext"/> is a separate
/// <see cref="ISessionEstablisher.EstablishAsync"/> parameter).
/// </summary>
/// <param name="UserId">
/// The authenticated user's stable id — becomes the <c>"sub"</c> claim when
/// <c>OnValidatePrincipal</c> rehydrates the principal from the record (ADR 0099 C5).
/// MUST be non-empty.
/// </param>
/// <param name="TenantId">
/// The bound tenant (strong-typed per ADR 0091 <c>TenantId.FromString</c> centralization).
/// MUST NOT be a system sentinel (ADR 0084 / <see cref="Harborline.Api.Foundation.Assets.Common.TenantId.IsSystemSentinel"/>).
/// </param>
/// <param name="Reason">Why the session is being established (drives the audit label).</param>
public sealed record SessionEstablishmentRequest(
    string UserId,
    TenantId TenantId,
    SessionEstablishmentReason Reason);

/// <summary>
/// The summary of an established session, returned to the caller for audit correlation.
/// Never carries the raw cookie (<c>SignInAsync</c> owns the cookie write).
/// </summary>
/// <param name="SessionId">
/// The opaque CSPRNG session id (≥128-bit, base64url; ADR 0099 S4). Used for audit
/// correlation only — it is the cookie's bearer value, so treat it as a secret in logs.
/// </param>
/// <param name="IssuedUtc">When the session was established.</param>
/// <param name="AbsoluteExpiryUtc">
/// The server-enforced absolute expiry (<see cref="IssuedUtc"/> + the configured absolute
/// lifetime; ADR 0099 S6). The session dies at this instant regardless of activity.
/// </param>
public sealed record SessionEstablishmentResult(
    string SessionId,
    DateTimeOffset IssuedUtc,
    DateTimeOffset AbsoluteExpiryUtc);
