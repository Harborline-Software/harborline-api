namespace Harborline.Api.Foundation.Scheduling;

/// <summary>
/// <see cref="IDueQueueQueryService"/> implementation that extends the
/// existing <see cref="IRruleExpansionService"/> rather than introducing a
/// second recurrence engine.
/// </summary>
public sealed class RruleDueQueueQueryService : IDueQueueQueryService
{
    private readonly IRruleExpansionService _rrule;

    public RruleDueQueueQueryService(IRruleExpansionService rrule)
    {
        _rrule = rrule ?? throw new ArgumentNullException(nameof(rrule));
    }

    /// <inheritdoc />
    public IReadOnlyList<DueQueueItem> Query(
        IEnumerable<DueQueueSchedule> schedules,
        IEnumerable<DueQueueCompletion> completionHistory,
        DateOnly windowStart,
        DateOnly windowEnd,
        DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(completionHistory);

        if (windowEnd < windowStart)
            throw new ArgumentException("Due-queue window end must be on or after its start.", nameof(windowEnd));

        var completed = completionHistory
            .Select(ValidateCompletion)
            .Select(static completion => new OccurrenceKey(
                completion.SubjectRef,
                completion.ScheduleRef,
                completion.DueDate))
            .ToHashSet();

        var due = new List<DueQueueItem>();

        foreach (var schedule in schedules)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            if (!schedule.IsActive)
                continue;

            ValidateSchedule(schedule);

            if (schedule.EndsOn is { } endsOn && endsOn < windowStart)
                continue;

            var expansionEnd = schedule.EndsOn is { } hardEnd && hardEnd < windowEnd
                ? hardEnd
                : windowEnd;

            // The RRULE service's "today" parameter is its lower-bound filter.
            // Anchor it at this query's window start so past-due occurrences are
            // retained; asOf is deliberately reserved for overdue calculation.
            var occurrences = _rrule.ExpandOccurrences(
                rrule: schedule.Rrule,
                start: schedule.StartsOn,
                end: expansionEnd,
                lookaheadDays: windowEnd.DayNumber - windowStart.DayNumber,
                leadDays: 0,
                today: windowStart,
                timezone: schedule.Timezone);

            foreach (var dueDate in occurrences)
            {
                if (dueDate < windowStart || dueDate > windowEnd)
                    continue;

                var key = new OccurrenceKey(schedule.SubjectRef, schedule.ScheduleRef, dueDate);
                if (completed.Contains(key))
                    continue;

                due.Add(new DueQueueItem(
                    schedule.SubjectRef,
                    schedule.ScheduleRef,
                    dueDate,
                    Math.Max(0, asOf.DayNumber - dueDate.DayNumber)));
            }
        }

        return due
            .OrderByDescending(static item => item.OverdueDays)
            .ThenBy(static item => item.DueDate)
            .ThenBy(static item => item.SubjectRef, StringComparer.Ordinal)
            .ThenBy(static item => item.ScheduleRef, StringComparer.Ordinal)
            .ToArray();
    }

    private static DueQueueSchedule ValidateSchedule(DueQueueSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.SubjectRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.ScheduleRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.Rrule);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.Timezone);

        if (schedule.EndsOn is { } endsOn && endsOn < schedule.StartsOn)
            throw new ArgumentException("Schedule end must be on or after its start.", nameof(schedule));

        return schedule;
    }

    private static DueQueueCompletion ValidateCompletion(DueQueueCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentException.ThrowIfNullOrWhiteSpace(completion.SubjectRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(completion.ScheduleRef);
        return completion;
    }

    private readonly record struct OccurrenceKey(
        string SubjectRef,
        string ScheduleRef,
        DateOnly DueDate);
}
