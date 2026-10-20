namespace Harborline.Api.Foundation.Scheduling;

/// <summary>
/// Compatibility facade over the pinned Platform recurrence producer. Existing API callers and DI keep
/// their interface and parameterless construction; expansion and strict/completeness refusals have one owner.
/// </summary>
public sealed class InMemoryRruleExpansionService : IRruleExpansionService
{
    private readonly global::Harborline.Foundation.Scheduling.InMemoryRruleExpansionService _platform = new();

    /// <inheritdoc />
    public IReadOnlyList<DateOnly> ExpandOccurrences(
        string rrule,
        DateOnly start,
        DateOnly? end,
        int lookaheadDays,
        int leadDays,
        DateOnly today,
        string timezone)
        => _platform.ExpandOccurrences(rrule, start, end, lookaheadDays, leadDays, today, timezone);
}
