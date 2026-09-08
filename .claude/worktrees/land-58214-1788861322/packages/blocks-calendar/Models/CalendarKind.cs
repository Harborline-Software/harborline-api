namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// What kind of owned <see cref="Calendar"/> collection this is (calendar-productization design
/// note §1.5). The three kinds cover the dogfood + near-GA needs; the collection is the primary
/// organizing unit the productized calendar surfaces, with the resource-lens demoted to
/// <see cref="Resource"/>.
/// </summary>
public enum CalendarKind
{
    /// <summary>
    /// A calendar owned by a principal ("My calendar"). The provisioned default is one of these
    /// (design §1.4). Multi-user per-principal personal calendars are deferred to the identity
    /// program (#118) — until then the sole founder owns the one default personal calendar.
    /// </summary>
    Personal = 0,

    /// <summary>
    /// A calendar owned by the tenant/org, visible to a group ("Care team", "Front desk"). Multi-user
    /// visibility is gated on the sharing ruling + #118; until then a Team calendar is simply an
    /// additional calendar the sole founder owns.
    /// </summary>
    Team = 1,

    /// <summary>
    /// A lens over a <see cref="ParticipantRef"/> (a doctor's time, a room) — the first-class
    /// promotion of today's resource-lens. <see cref="OwnedCalendar.ResourceRef"/> is set; its events
    /// are the ones where that resource participates.
    /// </summary>
    Resource = 2,
}
