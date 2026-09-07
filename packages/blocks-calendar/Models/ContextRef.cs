namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A <b>thin, opaque</b> reference to the <i>context</i> a <see cref="CalendarEvent"/> is scheduled
/// <i>against</i> — a position, a floor, a project, a case, a ward (Slice S3, the
/// <c>ScheduledAgainst</c> dimension). This is the calendar core's <b>coverage-support seam</b>: the
/// core stores and exposes "what is this event scheduled against" so a future coverage / rostering
/// overlay (Pattern C) can ask "what is scheduled against this floor" — but the core computes
/// <b>nothing</b> about coverage itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately opaque / NOT domain-coupled.</b> Unlike <see cref="ParticipantRef"/> (which has a
/// closed Party/Asset discriminator because the booking core <i>does</i> distinguish a person from a
/// room), a context is whatever a vertical Pack decides — a nursing Pack's "ICU ward", a PM Pack's
/// "project", a staffing Pack's "position". The calendar must not enumerate those kinds, so a
/// <see cref="ContextRef"/> is just a free-form <see cref="Kind"/> discriminator string + a
/// <see cref="Value"/> id string. The core never interprets either; it only stores them and answers
/// "give me the events whose <c>ScheduledAgainst</c> equals this ref" (an exact-match equality).
/// </para>
/// <para>
/// <b>How it supports coverage without the core knowing about coverage.</b> A coverage overlay needs
/// two things the core can give it generically: (1) a way to tag an event with the thing it staffs
/// (<c>ScheduledAgainst</c>), and (2) a way to enumerate the events scheduled against that thing
/// (<c>EventsForContext</c>). With those, the overlay computes coverage = (the qualified, present
/// participants scheduled against the context in a shift) vs. (its own coverage minimum) — entirely
/// outside the calendar. The calendar stays an intent-agnostic booking substrate that merely
/// <i>exposes the data</i> the overlay needs (schedule-feature design: "the core computes NOTHING
/// about coverage; it just EXPOSES the data a future coverage overlay needs").
/// </para>
/// <para>
/// <b>Value equality.</b> As a <c>record</c> over <c>(Kind, Value)</c> a context ref has
/// structural equality, so it is a valid dictionary key and the by-context query matches by value,
/// matching the <see cref="ParticipantRef"/> idiom.
/// </para>
/// </remarks>
public sealed record ContextRef
{
    private ContextRef(string kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>
    /// The free-form context <i>kind</i> discriminator — a Pack-defined category such as
    /// <c>position</c>, <c>floor</c>, <c>project</c>, <c>case</c>, <c>ward</c>. The calendar never
    /// enumerates or interprets this; it is part of the opaque ref's identity.
    /// </summary>
    public string Kind { get; }

    /// <summary>The context's id value (a project id, a floor id, …). Opaque to the calendar core.</summary>
    public string Value { get; }

    /// <summary>
    /// Create a context ref from a Pack-defined <paramref name="kind"/> + an id
    /// <paramref name="value"/>. Both must be non-empty; the calendar does not validate their content
    /// beyond that (the kind namespace belongs to the Pack).
    /// </summary>
    /// <exception cref="ArgumentException">Either argument is null/empty/whitespace.</exception>
    public static ContextRef Of(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Context kind must be non-empty.", nameof(kind));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Context value must be non-empty.", nameof(value));
        return new ContextRef(kind, value);
    }

    public override string ToString() => $"{Kind}:{Value}";
}
