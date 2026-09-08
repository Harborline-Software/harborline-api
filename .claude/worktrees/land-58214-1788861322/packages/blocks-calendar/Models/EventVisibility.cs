namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// The <b>detail-visibility</b> classification of a calendar event (Slice CALENDAR-LAYERS,
/// demand-side) — how much of a (per-resource, demand-side) personal appointment another principal
/// may see. A personal appointment is already expressible as a <see cref="Occupancy.Blocking"/>
/// occupancy event (S3); the calendar-layers addition is <i>visibility</i>: free/busy shows the slot
/// as BUSY to others, but the event's detail fields (title, description, location, participants) are
/// scoped — visible only to the owner / authorized principals.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the existing access model applied to event FIELDS, not a new scheduling concept</b>
/// (schedule-feature design: "Visibility = the existing grant model applied to event fields, NOT a
/// new scheduling concept"). The calendar core carries the thin classification + the owner ref; the
/// authorization <i>decision</i> ("may principal P see this event's detail?") is the fleet grant
/// model's job (ADR 0117 / blocks-access-grant), wired at the Bridge / vertical layer — see the
/// noted seam on <see cref="Harborline.Api.Blocks.Calendar.Services.IFreeBusyService"/> and
/// <see cref="Harborline.Api.Blocks.Calendar.Services.IEventDetailVisibilityPolicy"/>. The block deliberately
/// does NOT take a <c>ProjectReference</c> on the heavyweight grant substrate
/// (<c>IPermissionResolver</c>/<c>IGrantStore</c>/<c>ShipRole</c>) — that would drag the authorization
/// chain into a pure-domain block (the same wrong-dependency-direction the <see cref="ParticipantRef"/>
/// design avoids for <c>blocks-assets</c>). Instead the block ships a pluggable
/// <c>IEventDetailVisibilityPolicy</c> seam whose default is the owner-only rule; a host registers a
/// grant-backed policy.
/// </para>
/// <para>
/// <b>The litmus (why visibility is NOT a free/busy field but occupancy is).</b> Free/busy reasons
/// about <i>occupancy</i> (does the time block a booking?) — so occupancy is a first-class common
/// field. Free/busy does NOT reason about <i>who may read the title</i> — so detail-visibility is a
/// thin classification + a projection (<c>busy-to-others</c> redacts detail), not something the
/// interval math touches. A <see cref="Private"/> event still occupies the resource and still blocks
/// a competing booking; only its detail is redacted in another principal's view.
/// </para>
/// </remarks>
public enum EventVisibility
{
    /// <summary>
    /// The default — the event's detail is visible to anyone who can see the resource's calendar
    /// (a normal work appointment). Free/busy shows it as busy <i>with</i> its detail. The S0–S3
    /// behavior, unchanged.
    /// </summary>
    Public = 0,

    /// <summary>
    /// A private / personal appointment — the slot shows as <b>busy</b> to other principals (it still
    /// occupies the resource and blocks a competing booking), but the event's detail fields (title,
    /// description, location, participants) are <b>redacted</b> for anyone who is not the owner or an
    /// authorized principal. "Personal appointment" in another principal's free/busy view; the full
    /// detail in the owner's own view. The grant-backed authorization decision is wired at the host
    /// layer via <c>IEventDetailVisibilityPolicy</c>.
    /// </summary>
    Private = 1,
}
