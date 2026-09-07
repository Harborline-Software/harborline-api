namespace Harborline.Api.Foundation.Scheduling;

/// <summary>
/// Domain-neutral recurrence definition projected into the due-queue query.
/// The consuming pack decides what is schedulable and supplies opaque
/// <paramref name="SubjectRef"/> and <paramref name="ScheduleRef"/> values.
/// </summary>
public sealed record DueQueueSchedule(
    string SubjectRef,
    string ScheduleRef,
    string Rrule,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    string Timezone,
    bool IsActive);

/// <summary>
/// Completion-history marker for one expected schedule occurrence.
/// </summary>
/// <remarks>
/// <paramref name="DueDate"/> identifies the occurrence that was completed;
/// the completion's actual timestamp remains pack-owned and is not needed to
/// derive the queue.
/// </remarks>
public sealed record DueQueueCompletion(
    string SubjectRef,
    string ScheduleRef,
    DateOnly DueDate);

/// <summary>
/// An incomplete expected occurrence emitted by the derived due queue.
/// </summary>
public sealed record DueQueueItem(
    string SubjectRef,
    string ScheduleRef,
    DateOnly DueDate,
    int OverdueDays);

