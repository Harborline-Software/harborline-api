namespace Harborline.Api.UIAdapters.Blazor.Enums;

/// <summary>
/// Calendar view mode for <c>Harborline.Api.UIAdapters.Blazor.Components.Scheduling.HarborlineScheduler&lt;TEvent&gt;</c>.
/// </summary>
/// <remarks>
/// This is the canonical MVP enum used by the Scheduling-namespace <c>HarborlineScheduler</c>.
/// A separate <c>Harborline.Api.Foundation.Models.SchedulerView</c> (Day / Week / Month / MultiDay /
/// Timeline / Agenda) exists for the legacy DataDisplay <c>HarborlineScheduler</c>; the two enums
/// are independent and live in different namespaces by design (ADR 0022 Tier 3 scheduling
/// family). The MVP surface narrows the canonical set to the five views that every Harborline
/// scheduler adapter must support.
/// </remarks>
public enum SchedulerView
{
    /// <summary>Single-day view with hourly rows.</summary>
    Day,

    /// <summary>7-day view (Sun–Sat by default) with hourly rows per day column.</summary>
    Week,

    /// <summary>5-day work-week view (Mon–Fri) with hourly rows per day column.</summary>
    WorkWeek,

    /// <summary>Month calendar grid with events stacked per day cell.</summary>
    Month,

    /// <summary>Flat chronological list of upcoming events grouped by day.</summary>
    Agenda,
}
