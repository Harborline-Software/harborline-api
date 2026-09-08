namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A single busy occurrence in a <i>viewer-scoped</i> free/busy result (Slice CALENDAR-LAYERS) — the
/// occupied interval PLUS the detail the viewing principal is allowed to see. For a
/// <see cref="EventVisibility.Public"/> event (or a <see cref="EventVisibility.Private"/> event the
/// viewer is authorized for), <see cref="Title"/> carries the real title and <see cref="DetailVisible"/>
/// is <see langword="true"/>. For a <see cref="EventVisibility.Private"/> event the viewer is NOT
/// authorized for, the interval is preserved (the slot still shows BUSY) but <see cref="Title"/> is the
/// redacted placeholder and <see cref="DetailVisible"/> is <see langword="false"/> — busy-to-others,
/// detail-to-owner.
/// </summary>
/// <param name="Interval">The occupied UTC interval (always present — the slot is busy regardless of visibility).</param>
/// <param name="EventId">The source event's id (always present — an opaque handle the viewer can use to request detail through an authorized path; it leaks no detail).</param>
/// <param name="DetailVisible">True when the viewer may see this event's detail; false when it is redacted (busy-only).</param>
/// <param name="Title">The event title when <paramref name="DetailVisible"/>; otherwise the redacted placeholder (<see cref="RedactedTitle"/>).</param>
/// <param name="Visibility">The event's own visibility classification (so a caller can render "Private" affordances).</param>
public sealed record BusyEvent(
    TimeInterval Interval,
    CalendarEventId EventId,
    bool DetailVisible,
    string Title,
    EventVisibility Visibility)
{
    /// <summary>The placeholder title shown for a redacted (private, unauthorized-viewer) busy slot.</summary>
    public const string RedactedTitle = "Busy";

    /// <summary>
    /// Build a <b>detail-visible</b> busy event (the viewer is authorized — a public event, or a
    /// private event the viewer owns / holds a grant for).
    /// </summary>
    public static BusyEvent Visible(TimeInterval interval, CalendarEventId eventId, string title, EventVisibility visibility)
        => new(interval, eventId, DetailVisible: true, Title: title, Visibility: visibility);

    /// <summary>
    /// Build a <b>redacted</b> busy event — the slot shows BUSY (<see cref="Interval"/> preserved) but
    /// the title/detail is hidden (a private event the viewer is not authorized for).
    /// </summary>
    public static BusyEvent Redacted(TimeInterval interval, CalendarEventId eventId, EventVisibility visibility)
        => new(interval, eventId, DetailVisible: false, Title: RedactedTitle, Visibility: visibility);
}
