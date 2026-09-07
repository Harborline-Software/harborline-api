using System.Collections.Concurrent;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// Thread-safe in-memory <see cref="IFormSubmitOutbox"/> (ADR 0101 Rev 3.1 Wave 2b / F-ATOM). Keyed by
/// the form-instance id; enqueue is idempotent (a replay returns the existing row). Ordering is by a
/// monotonic append sequence so <see cref="ListUnresolvedAsync"/> returns oldest-first.
/// </summary>
/// <remarks>
/// The in-memory slice keeps the same host-swap posture as the rest of the Wave-1/2 substrate: a
/// production host replaces this with a persistence-backed transactional outbox behind the same
/// interface. Nothing here reads an ambient clock — the row carries the engine's submit instant.
/// </remarks>
public sealed class InMemoryFormSubmitOutbox : IFormSubmitOutbox
{
    private readonly ConcurrentDictionary<string, Slot> _rows = new(StringComparer.Ordinal);
    private long _sequence;

    private sealed record Slot(long Sequence, FormSubmitOutboxEntry Entry);

    /// <inheritdoc />
    public Task<FormSubmitOutboxEntry> EnqueueAsync(FormSubmitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var candidate = FormSubmitOutboxEntry.FromContext(context);

        // Idempotent by instance: a replay of the same submission returns the row already recorded,
        // never a second pending intent.
        var slot = _rows.GetOrAdd(candidate.Id, _ => new Slot(Interlocked.Increment(ref _sequence), candidate));
        return Task.FromResult(slot.Entry);
    }

    /// <inheritdoc />
    public Task MarkCompletedAsync(string entryId, CancellationToken cancellationToken = default)
    {
        Mutate(entryId, e => e with { State = FormSubmitOutboxState.Completed, LastError = null });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkFailedAsync(string entryId, string error, CancellationToken cancellationToken = default)
    {
        Mutate(entryId, e => e with
        {
            State = FormSubmitOutboxState.Failed,
            Attempts = e.Attempts + 1,
            LastError = error,
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FormSubmitOutboxEntry>> ListUnresolvedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FormSubmitOutboxEntry> unresolved = _rows.Values
            .Where(s => s.Entry.State != FormSubmitOutboxState.Completed)
            .OrderBy(s => s.Sequence)
            .Select(s => s.Entry)
            .ToList();
        return Task.FromResult(unresolved);
    }

    /// <inheritdoc />
    public Task<FormSubmitOutboxEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default)
        => Task.FromResult(_rows.TryGetValue(entryId, out var slot) ? slot.Entry : null);

    private void Mutate(string entryId, Func<FormSubmitOutboxEntry, FormSubmitOutboxEntry> update)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);
        _rows.AddOrUpdate(
            entryId,
            // A mark for an id that was never enqueued is a programming error — but never throw inside
            // the resilient run path; a missing row simply cannot be re-created from nothing here.
            _ => throw new InvalidOperationException($"Outbox entry '{entryId}' does not exist."),
            (_, slot) => slot with { Entry = update(slot.Entry) });
    }
}
