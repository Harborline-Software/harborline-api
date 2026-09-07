namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The durable at-least-once journal that makes post-submit projection recoverable (ADR 0101 Rev 3.1
/// Wave 2b / F-ATOM). A submission's projection intent is recorded here BEFORE the projection runs, so
/// a process that dies mid-projection leaves a <see cref="FormSubmitOutboxState.Pending"/> row a
/// reconcile sweep re-runs — the "diagnosable, recoverable trace" the gate requires.
/// </summary>
/// <remarks>
/// The in-memory implementation (<see cref="InMemoryFormSubmitOutbox"/>) is Development-only and its
/// registration fails startup closed in every other environment. A non-Development host wires this seam
/// onto a transactional outbox that writes the row in the SAME transaction as the submission (closing the
/// last non-atomic window the in-memory two-store slice cannot). Idempotent by instance id: re-enqueuing
/// the same submission returns the existing row rather than duplicating.
/// </remarks>
public interface IFormSubmitOutbox
{
    /// <summary>
    /// Records (or returns the existing) pending projection intent for a just-persisted submission.
    /// Called BEFORE the projection runs, so the durable row is the recoverable trace.
    /// </summary>
    Task<FormSubmitOutboxEntry> EnqueueAsync(FormSubmitContext context, CancellationToken cancellationToken = default);

    /// <summary>Marks a row <see cref="FormSubmitOutboxState.Completed"/> after its projection succeeded.</summary>
    Task MarkCompletedAsync(string entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a row <see cref="FormSubmitOutboxState.Failed"/> with the fault message and bumps its
    /// attempt count — the diagnosable trace for a projection that threw.
    /// </summary>
    Task MarkFailedAsync(string entryId, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rows a reconcile sweep must (re)drain — every <see cref="FormSubmitOutboxState.Pending"/>
    /// (crash-interrupted) or <see cref="FormSubmitOutboxState.Failed"/> (threw) row, oldest first.
    /// </summary>
    Task<IReadOnlyList<FormSubmitOutboxEntry>> ListUnresolvedAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a single row by id, or null when absent (diagnosis / test observability).</summary>
    Task<FormSubmitOutboxEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default);
}
