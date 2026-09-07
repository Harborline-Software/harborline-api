using System;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Session;

/// <summary>
/// The authoritative server-side session state (ADR 0099 D1). The cookie carries ONLY the
/// opaque <see cref="SessionId"/>; everything else — the bound tenant, the user, the TTL
/// gates, the roles to rehydrate — lives here, never in the cookie (A6). This is the record
/// <c>OnValidatePrincipal</c> looks up by <see cref="SessionId"/> on every authenticated
/// request to enforce TTL (S6), cross-check the tenant (S8), touch idle activity, and
/// rehydrate the principal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutable except by touch (ADR 0099 C2).</b> A <c>sealed record</c> with init-only
/// members; <see cref="LastSeenUtc"/> is the only field mutated during a session's life, and
/// even that is done by producing a NEW record via <c>with</c> (see <see cref="Touch"/>) —
/// never in-place. The in-memory store replaces the dictionary entry; the future backed
/// store does a DB update. No setter is exposed.
/// </para>
/// <para>
/// Roles are NOT stored on the record in v1: the v1 establishment paths (password login,
/// verify-email, magic-link) bind a session to a user + tenant, and role rehydration in the
/// production <c>SessionBackedTenantContext</c> (Bridge) reads roles from the user store at
/// validate time. If a later PR pins roles at establish time, add an init-only
/// <c>IReadOnlyList&lt;string&gt; Roles</c> member here; the <c>with</c>-based touch is
/// unaffected.
/// </para>
/// </remarks>
public sealed record SessionRecord
{
    /// <summary>
    /// The opaque CSPRNG session id (≥128-bit, base64url; ADR 0099 S4). The dictionary/DB key
    /// and the cookie's bearer value. Store LOOKUPS are by-key (no byte compare); only the
    /// S9 single-use-token consume path uses <c>FixedTimeEquals</c> (a separate concern).
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>The authenticated user's stable id — the <c>"sub"</c> claim on rehydrate (C5).</summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The tenant this session is bound to (strong-typed; ADR 0091 §2.2). Cross-checked
    /// against the subdomain-resolved tenant at <c>OnValidatePrincipal</c>, FAIL-CLOSED (S8).
    /// Established sessions never bind to a system sentinel (rejected at the establisher).
    /// </summary>
    public required TenantId TenantId { get; init; }

    /// <summary>When the session was established. Immutable.</summary>
    public required DateTimeOffset IssuedUtc { get; init; }

    /// <summary>
    /// The server-enforced absolute expiry instant (ADR 0099 S6). <c>OnValidatePrincipal</c>
    /// rejects when <c>now &gt; AbsoluteExpiryUtc</c>. Immutable — the absolute lifetime does
    /// not slide.
    /// </summary>
    public required DateTimeOffset AbsoluteExpiryUtc { get; init; }

    /// <summary>
    /// The last instant this session was seen on an authenticated request. The ONLY
    /// touch-mutable field (ADR 0099 C2) — slides the idle window (S6). Updated via
    /// <see cref="Touch"/> (produces a new record), never in place.
    /// </summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Why this session was established (drives the establish-time audit label).</summary>
    public required SessionEstablishmentReason Reason { get; init; }

    /// <summary>
    /// Returns a copy of this record with <see cref="LastSeenUtc"/> advanced to
    /// <paramref name="now"/>. The sliding-idle touch (S6) — immutable-record style; the store
    /// swaps the entry for the returned copy. All other fields are preserved.
    /// </summary>
    /// <param name="now">The current instant (the request time).</param>
    public SessionRecord Touch(DateTimeOffset now) => this with { LastSeenUtc = now };

    /// <summary>
    /// True when this session has passed its absolute lifetime OR its idle window, as of
    /// <paramref name="now"/>. This is the server-side TTL gate (ADR 0099 S6/B): the cookie
    /// <c>ExpireTimeSpan</c> is a redundant client hint — THIS record is the authority.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="idleTimeout">
    /// The sliding idle window; a session expires if <c>now - LastSeenUtc &gt; idleTimeout</c>.
    /// </param>
    public bool IsExpired(DateTimeOffset now, TimeSpan idleTimeout) =>
        now > AbsoluteExpiryUtc || (now - LastSeenUtc) > idleTimeout;
}
