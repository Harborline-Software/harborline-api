using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// Composable in-memory <see cref="ISubmissionDraftStore"/> reference implementation
/// (ADR 0135 amendment 2026-07-01 — D2). Correct for tests, single-process demos, and
/// as the <c>TryAdd</c> default a durable host overrides. It is process-volatile — it
/// does NOT survive a restart, so a production node MUST register a durable store for
/// genuine cross-device / restart-surviving resume.
/// </summary>
public sealed class InMemorySubmissionDraftStore : ISubmissionDraftStore
{
    private readonly ConcurrentDictionary<string, SubmissionDraft> _byKey = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task UpsertAsync(SubmissionDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ct.ThrowIfCancellationRequested();
        // Copy the body defensively so a caller mutating its buffer cannot alter the stored draft.
        var stored = draft with { Body = draft.Body.ToArray() };
        _byKey[draft.Key.ToStableString()] = stored;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SubmissionDraft?> GetAsync(SubmissionDraftKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_byKey.TryGetValue(key.ToStableString(), out var draft) ? draft : null);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(SubmissionDraftKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_byKey.TryRemove(key.ToStableString(), out _));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubmissionDraft>> ListByPartyAsync(TenantId tenant, Guid partyId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<SubmissionDraft> matches = _byKey.Values
            .Where(d => d.Key.Tenant.Equals(tenant) && d.Key.PartyId == partyId)
            .OrderByDescending(d => d.UpdatedAt)
            .ToList();
        return Task.FromResult(matches);
    }
}
