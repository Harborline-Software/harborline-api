using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// Orchestrates the pre-auth capture buffer's <b>promote-or-purge</b> lifecycle (ADR
/// 0135 amendment 2026-07-01 — D2): an unidentified actor's capture is buffered briefly,
/// then either PROMOTED to a keyed draft on sign-in or PURGED on abandon / TTL. This is
/// the seam that keeps unidentified PII from lingering ungoverned.
/// </summary>
public interface IPreAuthCaptureService
{
    /// <summary>
    /// Buffers an unidentified capture for <paramref name="session"/> with a short TTL
    /// (<paramref name="ttl"/>, defaulted when null). Requires no party — that is the point:
    /// the actor is not yet identified.
    /// </summary>
    Task CaptureAsync(
        CaptureSessionId session,
        FormDefinitionId formId,
        DraftCaseId caseId,
        SubmissionDraftProvenance provenance,
        ReadOnlyMemory<byte> body,
        string? subjectId = null,
        TimeSpan? ttl = null,
        CancellationToken ct = default);

    /// <summary>
    /// On identification, PROMOTES every buffered capture for <paramref name="session"/>
    /// into a keyed submission-draft (via the fail-closed <see cref="ISubmissionDraftService"/>),
    /// then clears the session from the buffer. If the actor is still unresolvable to a party,
    /// the fail-closed resolver throws and NOTHING is promoted or purged (the captures remain
    /// for a later identified promote or the TTL sweep). Returns the resolved draft keys.
    /// </summary>
    Task<IReadOnlyList<SubmissionDraftKey>> PromoteOnIdentifyAsync(CaptureSessionId session, CancellationToken ct = default);

    /// <summary>Purges every buffered capture for <paramref name="session"/> — the abandon path. Returns the count removed.</summary>
    Task<int> DiscardAsync(CaptureSessionId session, CancellationToken ct = default);

    /// <summary>Purges every expired capture across all sessions (the TTL sweep — no lingering PII). Returns the count removed.</summary>
    Task<int> SweepExpiredAsync(CancellationToken ct = default);
}
