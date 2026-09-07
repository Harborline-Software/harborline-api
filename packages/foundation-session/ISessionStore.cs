using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Session;

/// <summary>
/// Server-side session store (ADR 0099 D1). Holds the authoritative <see cref="SessionRecord"/>
/// per opaque session id; the cookie carries only the id. Owns create, by-key lookup, idle
/// touch, and revocation (S7). The v1 impl is <see cref="InMemorySessionStore"/>; a
/// SQLite/Postgres-backed impl follows in a later PR (mirroring the JournalStore
/// in-memory→backed progression).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ValueTask"/> returns (ADR 0099 C3).</b> The in-memory v1 completes
/// synchronously (so allocations stay near-zero), but the interface is async-ready for the
/// PR-5 backed store without a later breaking change.
/// </para>
/// <para>
/// <b>Atomicity.</b> The in-memory impl is <c>ConcurrentDictionary</c>-atomic; the future
/// backed impl MUST be DB-atomic for the same operations. By-key lookups need no constant-time
/// compare; the constant-time discipline (<c>FixedTimeEquals</c>) applies to the S9 single-use
/// <i>token</i> consume path (a separate verify/magic-link concern, not session-id lookup).
/// </para>
/// <para>
/// <b>Single-instance only in v1 (ADR 0099 O-5).</b> The in-memory store does not provide
/// cross-instance revocation durability; a scaled-out/HA Bridge requires the backed store
/// before it is multi-instance. A Bridge restart drops all sessions — a fail-SAFE mass
/// revocation, acceptable for MVP.
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>
    /// Persists a freshly-minted <see cref="SessionRecord"/>. The id is CSPRNG-unique
    /// (collision-negligible at ≥128-bit; ADR 0099 S4), so this is an add. Implementations
    /// throw if the id already exists — that would indicate an entropy failure, not a normal
    /// condition.
    /// </summary>
    /// <param name="record">The record to store. Its <see cref="SessionRecord.SessionId"/> is the key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if a record with the same id already exists.</exception>
    ValueTask CreateAsync(SessionRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up the record for a session id (by-key; no byte compare). Returns <c>null</c> if
    /// absent or revoked — the caller (<c>OnValidatePrincipal</c>) treats null as "reject +
    /// sign out" (S7). This method does NOT enforce TTL; expiry is the caller's gate against
    /// <see cref="SessionRecord.IsExpired"/> (so the caller can emit the right audit on the
    /// expiry branch).
    /// </summary>
    /// <param name="sessionId">The opaque session id from the cookie.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record, or <c>null</c> if no live record exists for the id.</returns>
    ValueTask<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Advances the record's <see cref="SessionRecord.LastSeenUtc"/> to <paramref name="now"/>
    /// (sliding idle, S6). No-op if the session is absent (already revoked/expired). Returns
    /// the touched record, or <c>null</c> if absent. Atomic against concurrent touches.
    /// </summary>
    /// <param name="sessionId">The session id to touch.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The touched record, or <c>null</c> if no record exists for the id.</returns>
    ValueTask<SessionRecord?> TouchAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a session server-side (ADR 0099 S7) — removes the record so a stolen cookie dies
    /// immediately, not just a cookie-clear. Idempotent: removing an absent session returns
    /// <c>false</c> without throwing. Backs logout, admin force-logout, and S1 fixation
    /// invalidation of an inbound id.
    /// </summary>
    /// <param name="sessionId">The session id to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if a record was removed; <c>false</c> if none existed.</returns>
    ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken);
}
