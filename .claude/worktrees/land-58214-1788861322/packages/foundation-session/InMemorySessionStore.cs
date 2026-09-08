using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Session;

/// <summary>
/// The v1 server-side session store (ADR 0099 D1/C3): a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// of opaque-session-id → <see cref="SessionRecord"/>. Production-and-test for v1; a
/// SQLite/Postgres-backed impl follows in a later PR (the JournalStore in-memory→backed
/// progression). Registered as a singleton (the dictionary IS the shared store).
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-instance only (ADR 0099 O-5 / §Consequences).</b> The dictionary is per-process:
/// a Bridge restart drops every session (fail-SAFE mass revocation, acceptable for MVP), and a
/// session/revocation on instance A is invisible to instance B — a scaled-out/HA Bridge REQUIRES
/// the backed store before it is multi-instance. This is a deployment-topology security
/// constraint, not just a durability nicety.
/// </para>
/// <para>
/// All operations are <c>ConcurrentDictionary</c>-atomic. Touch uses <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/>
/// in a compare-and-swap loop because <see cref="SessionRecord"/> is immutable (touch produces a
/// new record via <c>with</c>); the loop re-reads on a lost race so a concurrent touch never
/// silently drops a slide.
/// </para>
/// </remarks>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask CreateAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sessions.TryAdd(record.SessionId, record))
        {
            // A CSPRNG >=128-bit id collision is negligible; a duplicate here signals an
            // entropy/reuse failure rather than a normal condition — fail loud.
            throw new InvalidOperationException(
                "A session record already exists for the generated id — possible entropy failure or id reuse.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(sessionId))
        {
            return ValueTask.FromResult<SessionRecord?>(null);
        }

        return ValueTask.FromResult(_sessions.TryGetValue(sessionId, out var record) ? record : null);
    }

    /// <inheritdoc />
    public ValueTask<SessionRecord?> TouchAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(sessionId))
        {
            return ValueTask.FromResult<SessionRecord?>(null);
        }

        // Compare-and-swap loop: the record is immutable, so Touch returns a new instance;
        // TryUpdate only succeeds if the stored value is still the one we read. On a lost race
        // (a concurrent touch/remove changed the entry) we re-read and retry — or bail if it
        // was removed underneath us.
        while (_sessions.TryGetValue(sessionId, out var current))
        {
            var touched = current.Touch(now);
            if (_sessions.TryUpdate(sessionId, touched, current))
            {
                return ValueTask.FromResult<SessionRecord?>(touched);
            }
            // Lost the race; loop re-reads the latest entry.
        }

        return ValueTask.FromResult<SessionRecord?>(null);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(sessionId))
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(_sessions.TryRemove(sessionId, out _));
    }
}
