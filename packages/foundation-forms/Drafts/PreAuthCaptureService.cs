using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// Reference <see cref="IPreAuthCaptureService"/> (ADR 0135 amendment 2026-07-01 — D2).
/// Composes the ephemeral <see cref="IPreAuthCaptureBuffer"/> with the fail-closed
/// <see cref="ISubmissionDraftService"/> to realize promote-or-purge.
/// </summary>
public sealed class PreAuthCaptureService : IPreAuthCaptureService
{
    /// <summary>The default short TTL for a buffered capture (unidentified PII must not linger).</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private readonly IPreAuthCaptureBuffer _buffer;
    private readonly ISubmissionDraftService _drafts;
    private readonly TimeProvider _clock;

    /// <summary>Constructs the capture service over the buffer, the fail-closed draft service, and a clock.</summary>
    public PreAuthCaptureService(
        IPreAuthCaptureBuffer buffer,
        ISubmissionDraftService drafts,
        TimeProvider? clock = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public Task CaptureAsync(
        CaptureSessionId session,
        FormDefinitionId formId,
        DraftCaseId caseId,
        SubmissionDraftProvenance provenance,
        ReadOnlyMemory<byte> body,
        string? subjectId = null,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var now = _clock.GetUtcNow();
        var capture = new PreAuthCapture(
            Session: session,
            FormId: formId,
            Case: caseId,
            Provenance: provenance,
            Body: body,
            SubjectId: subjectId,
            CapturedAt: now,
            ExpiresAt: now + (ttl ?? DefaultTtl));
        return _buffer.CaptureAsync(capture, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubmissionDraftKey>> PromoteOnIdentifyAsync(CaptureSessionId session, CancellationToken ct = default)
    {
        // READ (don't take) first: if the actor is still unidentified, SaveDraftAsync's
        // fail-closed party resolution throws BEFORE we purge, so the captures survive for
        // a later identified promote (or the TTL sweep) — never silently lost, never mis-keyed.
        var captures = await _buffer.ReadSessionAsync(session, ct).ConfigureAwait(false);
        if (captures.Count == 0)
        {
            return Array.Empty<SubmissionDraftKey>();
        }

        var keys = new List<SubmissionDraftKey>(captures.Count);
        foreach (var capture in captures)
        {
            // Fail-closed: the FIRST call throws PrincipalPartyResolutionException if the
            // actor maps to no party — the whole promotion aborts and nothing is purged.
            var key = await _drafts.SaveDraftAsync(
                capture.FormId,
                capture.Case,
                capture.Provenance,
                capture.Body,
                capture.SubjectId,
                expiresAt: null,
                ct).ConfigureAwait(false);
            keys.Add(key);
        }

        // Only after every capture has been promoted to a keyed draft do we clear the buffer.
        await _buffer.PurgeSessionAsync(session, ct).ConfigureAwait(false);
        return keys;
    }

    /// <inheritdoc />
    public Task<int> DiscardAsync(CaptureSessionId session, CancellationToken ct = default) =>
        _buffer.PurgeSessionAsync(session, ct);

    /// <inheritdoc />
    public Task<int> SweepExpiredAsync(CancellationToken ct = default) =>
        _buffer.SweepExpiredAsync(_clock.GetUtcNow(), ct);
}
