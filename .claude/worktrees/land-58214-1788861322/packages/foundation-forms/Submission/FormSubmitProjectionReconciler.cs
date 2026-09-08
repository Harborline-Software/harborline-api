namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// Drains the projection outbox (ADR 0101 Rev 3.1 Wave 2b / F-ATOM) — the recovery half of the gate.
/// A host runs this on startup and/or on a periodic sweep: it re-runs every unresolved
/// (<see cref="FormSubmitOutboxState.Pending"/> crash-interrupted or <see cref="FormSubmitOutboxState.Failed"/>
/// threw) submission's projection through the core runner and marks the row completed on success. Replay
/// is idempotent by construction (the projector derives its side-record id deterministically), so a
/// row that partially applied before a crash converges rather than duplicating.
/// </summary>
public interface IFormSubmitProjectionReconciler
{
    /// <summary>
    /// Re-runs every unresolved outbox row and returns the count that recovered (completed) this sweep.
    /// A row that throws again is left Failed (with the fresh error) for the next sweep — never lost.
    /// </summary>
    Task<int> ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IFormSubmitProjectionReconciler"/> over the core runner + the outbox.</summary>
public sealed class FormSubmitProjectionReconciler : IFormSubmitProjectionReconciler
{
    private readonly FormSubmitProjectionRunner _inner;
    private readonly IFormSubmitOutbox _outbox;

    /// <summary>Creates the reconciler over the core projection runner and the durable outbox.</summary>
    public FormSubmitProjectionReconciler(FormSubmitProjectionRunner inner, IFormSubmitOutbox outbox)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    /// <inheritdoc />
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var unresolved = await _outbox.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false);
        var recovered = 0;

        foreach (var entry in unresolved)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var context = entry.RebuildContext();
            try
            {
                await _inner.RunAsync(context, cancellationToken).ConfigureAwait(false);
                await _outbox.MarkCompletedAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                recovered++;
            }
            catch (Exception ex)
            {
                // Still failing — keep the diagnosable trace, move on. Next sweep retries.
                await _outbox.MarkFailedAsync(entry.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                context.SubmittedValues.Dispose();
            }
        }

        return recovered;
    }
}
