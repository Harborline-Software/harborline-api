using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// In-memory <see cref="IPreAuthCaptureBuffer"/> — the correct default: the pre-auth
/// buffer is meant to be ephemeral and short-lived. A capture that is never promoted is
/// swept on TTL; nothing unidentified is ever written to durable storage.
/// </summary>
public sealed class InMemoryPreAuthCaptureBuffer : IPreAuthCaptureBuffer
{
    // session -> (case -> capture). Inner dict keyed by case so a re-capture is last-write-wins.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PreAuthCapture>> _bySession =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task CaptureAsync(PreAuthCapture capture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ct.ThrowIfCancellationRequested();
        var session = _bySession.GetOrAdd(capture.Session.Value, _ => new ConcurrentDictionary<string, PreAuthCapture>(StringComparer.Ordinal));
        // Defensive copy of the body so a caller mutating its buffer cannot alter the entry.
        session[capture.Case.Value] = capture with { Body = capture.Body.ToArray() };
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PreAuthCapture>> ReadSessionAsync(CaptureSessionId session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<PreAuthCapture> captures = _bySession.TryGetValue(session.Value, out var entries)
            ? entries.Values.OrderByDescending(c => c.CapturedAt).ToList()
            : Array.Empty<PreAuthCapture>();
        return Task.FromResult(captures);
    }

    /// <inheritdoc />
    public Task<int> PurgeSessionAsync(CaptureSessionId session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_bySession.TryRemove(session.Value, out var removed) ? removed.Count : 0);
    }

    /// <inheritdoc />
    public Task<int> SweepExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var purged = 0;
        foreach (var (sessionId, entries) in _bySession)
        {
            foreach (var (caseId, capture) in entries)
            {
                if (capture.ExpiresAt <= asOf && entries.TryRemove(caseId, out _))
                {
                    purged++;
                }
            }
            // Drop an emptied session bucket so it does not leak.
            if (entries.IsEmpty)
            {
                _bySession.TryRemove(sessionId, out _);
            }
        }
        return Task.FromResult(purged);
    }
}
