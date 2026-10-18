using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Scheduling;

/// <summary>
/// T312 consumes the real pinned producer through the existing API facade, DI, due queue and workflow admission.
/// Oracles are literal calendar dates, declared strict exceptions and complete-or-exception metadata, not a comparison
/// to another invocation of the same producer. No Scheduling mapping, booking, timezone or host policy is added.
/// </summary>
public sealed class StrictRecurrenceConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void default_and_repeated_DI_registration_keep_the_same_port_and_correct_biweekly_dates(bool fromDi)
    {
        var services = new ServiceCollection();
        services.AddFoundationScheduling();
        services.AddFoundationScheduling();
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IRruleExpansionService>());
        Assert.Single(provider.GetServices<IDueQueueQueryService>());
        IRruleExpansionService expansion = fromDi
            ? provider.GetRequiredService<IRruleExpansionService>()
            : new InMemoryRruleExpansionService();

        var dates = expansion.ExpandOccurrences("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=4",
            new(2026, 1, 5), new(2026, 1, 31), 0, 0, new(2026, 1, 5), "UTC");

        Assert.Equal(new DateOnly[] { new(2026, 1, 5), new(2026, 1, 7), new(2026, 1, 19), new(2026, 1, 21) }, dates);
    }

    [Fact]
    public void anchor_horizon_and_lead_remain_distinct_and_the_explicit_end_overrides_lookahead()
    {
        IRruleExpansionService expansion = new InMemoryRruleExpansionService();
        var horizon = expansion.ExpandOccurrences("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=4",
            new(2026, 1, 5), null, 16, 2, new(2026, 1, 6), "UTC");
        Assert.Equal(new DateOnly[] { new(2026, 1, 19), new(2026, 1, 21) }, horizon);
        var hardEnd = expansion.ExpandOccurrences("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=4",
            new(2026, 1, 5), new(2026, 1, 20), 99, 2, new(2026, 1, 6), "UTC");
        Assert.Equal(new DateOnly[] { new(2026, 1, 19) }, hardEnd);
    }

    [Theory]
    [InlineData("FREQ=DAILY;EXDATE=20260106")]
    [InlineData("FREQ=WEEKLY;BYMONTHDAY=1")]
    public void unsupported_restrictions_are_not_silently_dropped(string rule)
        => Assert.Throws<NotSupportedException>(() => new InMemoryRruleExpansionService().ExpandOccurrences(
            rule, new(2026, 1, 5), new(2026, 1, 7), 0, 0, new(2026, 1, 5), "UTC"));

    [Fact]
    public void malformed_supported_parts_are_not_replaced_with_defaults()
        => Assert.Throws<FormatException>(() => new InMemoryRruleExpansionService().ExpandOccurrences(
            "FREQ=DAILY;INTERVAL=0", new(2026, 1, 5), new(2026, 1, 7), 0, 0, new(2026, 1, 5), "UTC"));

    [Fact]
    public void a_complete_thousand_dates_returns_but_an_incomplete_range_throws_the_unwrapped_producer_exception()
    {
        var expansion = new InMemoryRruleExpansionService();
        var complete = expansion.ExpandOccurrences("FREQ=DAILY", new(2026, 1, 1), new(2028, 9, 26),
            0, 0, new(2026, 1, 1), "UTC");
        Assert.Equal(1000, complete.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), complete[0]);
        Assert.Equal(new DateOnly(2028, 9, 26), complete[^1]);

        var error = Assert.Throws<global::Harborline.Foundation.Scheduling.RruleExpansionCapExceededException>(() =>
            expansion.ExpandOccurrences("FREQ=DAILY", new(2026, 1, 1), new(2028, 9, 27),
                0, 0, new(2026, 1, 1), "UTC"));

        Assert.Equal("FREQ=DAILY", error.RecurrenceId);
        Assert.Equal(new DateOnly(2026, 1, 1), error.Anchor);
        Assert.Equal(new DateOnly(2026, 1, 1), error.RequestedRangeStart);
        Assert.Equal(new DateOnly(2028, 9, 27), error.RequestedRangeEnd);
        Assert.Equal(1000, error.CandidateLimit);
        Assert.Equal(1001, error.CandidatesExamined);
        Assert.Equal(new DateOnly(2028, 9, 27), error.LastEvaluatedOccurrence);
        Assert.Equal("None", error.Bound.ToString());

        var countBound = expansion.ExpandOccurrences("FREQ=DAILY;COUNT=1000", new(2026, 1, 1), new(2028, 9, 27),
            0, 0, new(2026, 1, 1), "UTC");
        Assert.Equal(1000, countBound.Count);
        Assert.Equal(new DateOnly(2028, 9, 26), countBound[^1]);
    }

    [Fact]
    public void actual_DI_due_queue_filters_completion_and_orders_literal_obligations()
    {
        var services = new ServiceCollection();
        services.AddFoundationScheduling();
        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IDueQueueQueryService>();
        DueQueueSchedule[] schedules = [new("subject-A", "schedule-A", "FREQ=DAILY;COUNT=3",
            new(2026, 1, 5), null, "UTC", true)];

        var due = queue.Query(schedules, [new("subject-A", "schedule-A", new(2026, 1, 6))],
            new(2026, 1, 5), new(2026, 1, 7), new(2026, 1, 8));

        Assert.Equal(new[] { ("subject-A", "schedule-A", new DateOnly(2026, 1, 5), 3),
            ("subject-A", "schedule-A", new DateOnly(2026, 1, 7), 1) },
            due.Select(item => (item.SubjectRef, item.ScheduleRef, item.DueDate, item.OverdueDays)));
        DueQueueSchedule[] incomplete = [schedules[0], new("subject-B", "schedule-B",
            "FREQ=DAILY;EXDATE=20260106", new(2026, 1, 5), null, "UTC", true)];
        Assert.Throws<NotSupportedException>(() => queue.Query(incomplete, [],
            new(2026, 1, 5), new(2026, 1, 7), new(2026, 1, 8)));
    }

    [Theory]
    [InlineData("FREQ=DAILY;EXDATE=20260106")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=WEEKLY;BYMONTHDAY=1")]
    public void actual_default_workflow_admission_rejects_invalid_schedule_rules_with_the_existing_code(string rule)
    {
        var admission = new WorkflowAdmissionValidator();
        var result = admission.Validate(Scheduled(rule));
        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("workflow.admission.schedule_rrule_invalid", violation.Code);
        Assert.Equal("daily", violation.Locator);
        Assert.True(admission.Validate(Scheduled("FREQ=DAILY;COUNT=3")).IsValid);
    }

    private static WorkflowDefinition Scheduled(string rule) => new()
    {
        Tenant = "local", Key = "strict-scheduled-definition", Version = "1.0.0", InitialState = "waiting",
        States = [new() { Id = "waiting" }, new() { Id = "done", Kind = WorkflowStateKind.Terminal }],
        Triggers = [new() { Id = "daily", Kind = WorkflowTriggerKind.Schedule, Rrule = rule }],
        Transitions = [new() { Id = "finish", From = "waiting", On = "daily", To = "done" }],
    };
}
