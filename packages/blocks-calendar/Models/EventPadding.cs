namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// The <b>time-footprint envelope</b> of a <see cref="CalendarEvent"/> (the padding slice) — the
/// pre- and post-padding durations that separate the event's <i>visible</i> (booked / shown)
/// interval from its <i>occupied</i> (resource-blocking) interval. An event is <b>not</b> a pure
/// <c>[start,end]</c> block: the patient sees the visible interval (2:00–2:30); the resource is
/// blocked over the occupied interval (1:55–3:00 with 5-min pre + 30-min post).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pre-padding</b> = the transition / travel / setup time <i>before</i> the visible start
/// (the doctor reading the chart, the field-service tech driving to the site, the room being set up).
/// <b>Post-padding</b> = the documentation / cleanup / turnover time <i>after</i> the visible end
/// (charting the visit, sterilizing the equipment, the room being reset). Both extend the
/// resource-occupied span beyond what the demand side sees or books.
/// </para>
/// <para>
/// <b>Common mechanism, configured values.</b> The pre/post <i>fields</i> + the occupied-interval
/// math are built into the booking substrate (free/busy and no-double-book must reason about the
/// occupied span — the litmus for a first-class common field). The <i>values</i> come from
/// configuration: an <see cref="Services.IPaddingPolicy"/> resolves a default per resource /
/// event-type / domain ("doctor appt = 5 pre + 30 post"), applied at booking, and any single event
/// can override it. <see cref="None"/> (0 pre + 0 post) is the backward-compatible default: an event
/// with no padding occupies exactly its visible interval, behaving as before this slice.
/// </para>
/// <para>
/// <b>Generalizes to coverage turnover.</b> A shift's pre/post-padding is its handoff /
/// documentation overlap; turnover overlap (an incoming shift's pre-padding overlapping the outgoing
/// shift's post-padding) falls out of the occupied-interval math for free — the same field, a
/// configured value, no coverage-specific code in the core.
/// </para>
/// </remarks>
/// <param name="Pre">
/// The pre-padding duration — added <i>before</i> the visible start to form the occupied start.
/// Non-negative; <see cref="TimeSpan.Zero"/> = no pre-padding.
/// </param>
/// <param name="Post">
/// The post-padding duration — added <i>after</i> the visible end to form the occupied end.
/// Non-negative; <see cref="TimeSpan.Zero"/> = no post-padding.
/// </param>
public readonly record struct EventPadding(TimeSpan Pre, TimeSpan Post)
{
    /// <summary>No padding (0 pre + 0 post) — the backward-compatible default: the occupied interval equals the visible interval.</summary>
    public static readonly EventPadding None = new(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>True when both pre and post are zero (the event occupies exactly its visible interval).</summary>
    public bool IsNone => Pre == TimeSpan.Zero && Post == TimeSpan.Zero;

    /// <summary>
    /// Create a validated padding envelope. Both durations must be non-negative (padding extends the
    /// occupied span; a negative "padding" would shrink it below the visible interval, which the
    /// visible/occupied model forbids — the occupied interval always contains the visible one).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Either <paramref name="pre"/> or <paramref name="post"/> is negative.</exception>
    public static EventPadding Of(TimeSpan pre, TimeSpan post)
    {
        if (pre < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pre), pre, "Pre-padding must be non-negative.");
        if (post < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(post), post, "Post-padding must be non-negative.");
        return new EventPadding(pre, post);
    }
}
