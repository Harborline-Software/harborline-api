namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The lifecycle of a <see cref="FormSubmitOutboxEntry"/> — the durable at-least-once record that
/// closes ADR 0101 Rev 3.1 Wave 2b gate <b>F-ATOM</b> (a committed submission whose projection is
/// interrupted must leave a diagnosable, recoverable trace).
/// </summary>
public enum FormSubmitOutboxState
{
    /// <summary>
    /// Recorded with the submission, projection not yet confirmed. A process that dies here (between
    /// the submission committing and the projection completing) leaves the row in this state, so a
    /// reconcile sweep can re-run it — the recoverable trace.
    /// </summary>
    Pending = 0,

    /// <summary>The projection ran to completion; the side records are durable. A terminal state.</summary>
    Completed = 1,

    /// <summary>
    /// The projection threw. The row carries the error for diagnosis and is retried by the reconcile
    /// sweep (idempotent replay upserts, never duplicates — see <c>ConditionAssessmentId.Derive</c>).
    /// </summary>
    Failed = 2,
}
