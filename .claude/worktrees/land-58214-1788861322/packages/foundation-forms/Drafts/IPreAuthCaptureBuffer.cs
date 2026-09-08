using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// The short-TTL <b>pre-auth capture buffer</b> (ADR 0135 amendment 2026-07-01 — D2):
/// a thin, ephemeral store that holds an unidentified session's in-progress captures
/// until identification <em>promotes</em> them to keyed drafts, or a sweep
/// <em>purges</em> them. Deliberately volatile and short-lived — unidentified PII must
/// never linger in durable, ungoverned storage.
/// </summary>
public interface IPreAuthCaptureBuffer
{
    /// <summary>Buffers a capture (last-write-wins per <c>(session, case)</c>).</summary>
    Task CaptureAsync(PreAuthCapture capture, CancellationToken ct = default);

    /// <summary>Reads (without removing) all live captures for <paramref name="session"/>, most-recent first.</summary>
    Task<IReadOnlyList<PreAuthCapture>> ReadSessionAsync(CaptureSessionId session, CancellationToken ct = default);

    /// <summary>Purges (removes) every capture for <paramref name="session"/> — the abandon path. Returns the count removed.</summary>
    Task<int> PurgeSessionAsync(CaptureSessionId session, CancellationToken ct = default);

    /// <summary>
    /// Purges every capture whose <see cref="PreAuthCapture.ExpiresAt"/> is at or before
    /// <paramref name="asOf"/> across all sessions — the TTL sweep guaranteeing no
    /// lingering unidentified PII. Returns the count removed.
    /// </summary>
    Task<int> SweepExpiredAsync(DateTimeOffset asOf, CancellationToken ct = default);
}
