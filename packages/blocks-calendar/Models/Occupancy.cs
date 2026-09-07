namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// How a <see cref="CalendarEvent"/> occupies its resource's time — the <b>common</b> occupancy
/// classification free/busy needs (Slice S3). This is the minimal classification the operational
/// (free/busy) consumer requires; it is deliberately <i>not</i> the full domain
/// <c>EventType</c> / vertical intent (medical-appointment vs. meeting vs. project-task), and it is
/// <i>not</i> the vertical billable / productive flags — those are deferred overlays
/// (schedule-feature design: "occupancy = common; billable/productive = vertical").
/// </summary>
/// <remarks>
/// <para>
/// Free/busy is computed as <c>availability windows − ALL occupancy (Bookable AND Blocking)</c>: a
/// doctor's lunch is a <see cref="Blocking"/> event that removes the slot from free/busy even though
/// it is not an appointment, and a booked appointment is a <see cref="Bookable"/> event that consumes
/// availability. Both make the resource <i>busy</i>; the distinction is whether the time was a
/// bookable appointment of the resource (<see cref="Bookable"/>) or an internal block on it
/// (<see cref="Blocking"/>).
/// </para>
/// <para>
/// <b>Why this and not the full EventType.</b> The calendar core stays a "dumb correct booking
/// substrate" (schedule-feature design): it carries only the thin classification its own queries
/// need. <see cref="Occupancy"/> is exactly what free/busy needs and no more. A vertical Pack layers
/// the typed <c>EventType</c> + payload + billable/productive flags on top; the calendar never learns
/// what "billable" means.
/// </para>
/// </remarks>
public enum Occupancy
{
    /// <summary>
    /// An appointment / booking — it <b>consumes availability</b> (it was booked into a free slot)
    /// and makes the resource busy. The doctor's patient appointment, the booked conference room.
    /// The default occupancy of an event created by the booking path.
    /// </summary>
    Bookable = 0,

    /// <summary>
    /// An internal block — busy but <b>NOT bookable</b> (the resource cannot take an appointment in
    /// this time). The doctor's lunch, an admin block, a vacation. It removes time from free/busy
    /// (it makes the resource busy) but it is not an appointment of the resource. The canonical
    /// alternative to a recurring availability-exception for "the doctor's lunch shows".
    /// </summary>
    Blocking = 1,

    /// <summary>
    /// A tentatively-held slot — busy for free/busy purposes (it tentatively occupies the resource)
    /// but not yet a firm booking. A hold that has not been confirmed. Treated as busy by free/busy
    /// so a tentative hold blocks a competing booking from the same slot.
    /// </summary>
    Tentative = 2,
}
