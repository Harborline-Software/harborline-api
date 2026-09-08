namespace Harborline.Api.Foundation.Scheduling;

/// <summary>
/// Derives incomplete due occurrences from active RRULE schedules and
/// completion history.
/// </summary>
/// <remarks>
/// This is the thin booking/projection layer described by
/// <c>_shared/research/scheduling-activation-onr-2026-07-17.md §1.0</c>:
/// supply recurrence remains the shipped wall-clock/RRULE capability, while
/// packs bind the domain-specific meaning of a schedulable subject.
/// </remarks>
public interface IDueQueueQueryService
{
    /// <summary>
    /// Expand active schedules over the inclusive window, remove occurrences
    /// present in <paramref name="completionHistory"/>, and order overdue
    /// items ahead of current/future due items.
    /// </summary>
    /// <param name="schedules">Schedule definitions supplied by the consuming pack.</param>
    /// <param name="completionHistory">Completed occurrence identities.</param>
    /// <param name="windowStart">Inclusive first due date to consider.</param>
    /// <param name="windowEnd">Inclusive last due date to consider.</param>
    /// <param name="asOf">
    /// Frozen wall-clock date used only to calculate overdue days.
    /// </param>
    IReadOnlyList<DueQueueItem> Query(
        IEnumerable<DueQueueSchedule> schedules,
        IEnumerable<DueQueueCompletion> completionHistory,
        DateOnly windowStart,
        DateOnly windowEnd,
        DateOnly asOf);
}

