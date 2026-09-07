namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The durable <see cref="IFormSubmitProjectionRunner"/> that closes ADR 0101 Rev 3.1 Wave 2b gate
/// <b>F-ATOM</b>: it records a <see cref="FormSubmitOutboxState.Pending"/> outbox row BEFORE running
/// the projections, so a committed submission whose projection is interrupted (a throw, or a hard
/// process death mid-run) leaves a diagnosable, recoverable trace instead of silently losing the side
/// record. It wraps the core <see cref="FormSubmitProjectionRunner"/> (which iterates the registered
/// projections); the reconcile sweep (<see cref="FormSubmitProjectionReconciler"/>) re-runs any
/// unresolved row through that same core runner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering (the recoverable-trace invariant):</b> enqueue → run → mark. The enqueue completes
/// before the projections run, so:
/// <list type="bullet">
///   <item>a projection THROW ⇒ the row is marked <see cref="FormSubmitOutboxState.Failed"/> (with the
///     error) and the throw surfaces to the caller — the submission already committed, so an operator
///     must see the fault (the <c>IFormSubmitProjection</c> contract);</item>
///   <item>a hard process DEATH between enqueue and mark ⇒ the row stays
///     <see cref="FormSubmitOutboxState.Pending"/>, which the reconcile sweep drains.</item>
/// </list>
/// Replay is safe because the shipped projector derives its side-record id deterministically
/// (<c>ConditionAssessmentId.Derive</c>), so re-running upserts rather than duplicating.
/// </para>
/// <para>
/// The residual non-atomic window — between the submission committing and the enqueue completing — is
/// irreducible for a two-store in-memory slice; a production transactional outbox writes the row in the
/// submission's own transaction and closes it. Documented on <see cref="IFormSubmitOutbox"/>.
/// </para>
/// </remarks>
public sealed class OutboxFormSubmitProjectionRunner : IFormSubmitProjectionRunner
{
    private readonly FormSubmitProjectionRunner _inner;
    private readonly IFormSubmitOutbox _outbox;

    /// <summary>Wraps the core projection runner with the durable outbox.</summary>
    public OutboxFormSubmitProjectionRunner(FormSubmitProjectionRunner inner, IFormSubmitOutbox outbox)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormSubmitProjectionSkip>> RunAsync(
        FormSubmitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Durable trace FIRST — before any projection runs, so a crash leaves a Pending row to recover.
        var entry = await _outbox.EnqueueAsync(context, cancellationToken).ConfigureAwait(false);

        try
        {
            // A skip is a graceful, audited outcome (not a throw) — the row still marks Completed, and the
            // reported skips flow up so the route can surface them (F3).
            var skips = await _inner.RunAsync(context, cancellationToken).ConfigureAwait(false);
            await _outbox.MarkCompletedAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            return skips;
        }
        catch (Exception ex)
        {
            // Record the fault for reconcile + diagnosis, then surface it: the submission committed, so
            // the caller must see that its projection did not (the fail-safe contract). Cancellation
            // is recorded the same way — the row remains unresolved and is retried.
            await _outbox.MarkFailedAsync(entry.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
