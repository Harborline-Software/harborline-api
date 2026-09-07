namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// The role a participant plays in a <see cref="CalendarEvent"/> (Slice S2). Generalizes the
/// keystone domains: in a clinic appointment the doctor is a <see cref="Resource"/> (a Party — the
/// scarce booked thing whose time the no-double-book reservation protects), the patient is an
/// <see cref="Attendee"/> (a Party), and the exam room is a <see cref="Resource"/> (an Asset).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resource vs. Attendee — the load-bearing distinction.</b> A <see cref="Resource"/> is the
/// <i>scarce booked thing</i> (a doctor's time, a room, equipment) — it is reserved, it does not
/// "decline" an invitation; its status tracks whether the booking is held tentatively or confirmed.
/// An <see cref="Attendee"/> is invited and <i>RSVPs</i> (accept / decline / tentative). An
/// <see cref="Organizer"/> owns the invitation; an <see cref="Owner"/> owns the underlying
/// resource/calendar. The role determines which <see cref="ParticipationStatus"/> values are legal
/// (see <see cref="CalendarParticipation.Create"/>).
/// </para>
/// </remarks>
public enum ParticipationRole
{
    /// <summary>The event organizer — owns the invitation / the meeting itself.</summary>
    Organizer = 0,

    /// <summary>The owner of the underlying resource or calendar the event sits on.</summary>
    Owner = 1,

    /// <summary>An invited participant who RSVPs (the patient, a meeting guest). A Party.</summary>
    Attendee = 2,

    /// <summary>
    /// The scarce booked resource — a doctor's time (Party) or a room / equipment (Asset). Reserved,
    /// not RSVP'd: it is held <see cref="ParticipationStatus.Tentative"/> or
    /// <see cref="ParticipationStatus.Confirmed"/>, never <see cref="ParticipationStatus.Declined"/>.
    /// </summary>
    Resource = 3,
}

/// <summary>
/// A participant's response / booking state for a <see cref="CalendarEvent"/> (Slice S2). The legal
/// set depends on the <see cref="ParticipationRole"/>:
/// <list type="bullet">
///   <item><description><b>Attendees</b> (and Organizer / Owner) RSVP:
///   <see cref="Invited"/> → <see cref="Accepted"/> / <see cref="Declined"/> /
///   <see cref="Tentative"/> (the RFC 5545 <c>PARTSTAT</c> states).</description></item>
///   <item><description><b>Resources</b> are reserved, not invited: <see cref="Tentative"/> (held)
///   or <see cref="Confirmed"/> (booked). A resource is never <see cref="Invited"/> or
///   <see cref="Declined"/>.</description></item>
/// </list>
/// </summary>
public enum ParticipationStatus
{
    /// <summary>Invitation sent, no response yet (RFC 5545 NEEDS-ACTION). Attendee/Organizer/Owner only.</summary>
    Invited = 0,

    /// <summary>The attendee accepted (RFC 5545 ACCEPTED). Attendee/Organizer/Owner only.</summary>
    Accepted = 1,

    /// <summary>The attendee declined (RFC 5545 DECLINED). Attendee/Organizer/Owner only — never a Resource.</summary>
    Declined = 2,

    /// <summary>
    /// Tentative — an attendee tentatively accepted, or a resource booking is held but not yet
    /// confirmed (RFC 5545 TENTATIVE). Legal for every role.
    /// </summary>
    Tentative = 3,

    /// <summary>The resource booking is confirmed (the slot is held firm). Resource role only.</summary>
    Confirmed = 4,
}

/// <summary>
/// A single participant in a <see cref="CalendarEvent"/> (Slice S2) — a Party (person /
/// organization) or an Asset (room / equipment), with a <see cref="ParticipationRole"/> and a
/// <see cref="ParticipationStatus"/>. The realization of the §2.8.1 meeting↔participant tie that S0
/// shaped as an opaque seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant: role↔status legality.</b> The participant's status must be legal for its role — a
/// <see cref="ParticipationRole.Resource"/> cannot be <see cref="ParticipationStatus.Invited"/> or
/// <see cref="ParticipationStatus.Declined"/> (a room is reserved, it does not decline); an RSVP
/// role (<see cref="ParticipationRole.Attendee"/> / <see cref="ParticipationRole.Organizer"/> /
/// <see cref="ParticipationRole.Owner"/>) cannot be <see cref="ParticipationStatus.Confirmed"/>
/// (that is the resource-booking state). The invariant is enforced at construction by
/// <see cref="Create"/> — the constructor is non-public so an illegal pair is unrepresentable.
/// </para>
/// <para>
/// <b>Identity.</b> A participation is identified within an event by its
/// <see cref="Participant"/> + <see cref="Role"/> (the same Party may appear once as an Attendee and
/// once as a Resource — a doctor who is both the booked resource and an attendee of their own staff
/// meeting). <see cref="WithStatus"/> produces the next state on the same participant+role (RSVP
/// transitions), re-validating the invariant.
/// </para>
/// </remarks>
public sealed record CalendarParticipation
{
    private CalendarParticipation(ParticipantRef participant, ParticipationRole role, ParticipationStatus status)
    {
        Participant = participant;
        Role = role;
        Status = status;
    }

    /// <summary>The Party or Asset this participation attaches to.</summary>
    public ParticipantRef Participant { get; }

    /// <summary>The role the participant plays (Organizer / Owner / Attendee / Resource).</summary>
    public ParticipationRole Role { get; }

    /// <summary>The participant's RSVP / booking state, legal for its <see cref="Role"/>.</summary>
    public ParticipationStatus Status { get; }

    /// <summary>
    /// Create a participation, validating the role↔status invariant. Throws when the status is not
    /// legal for the role (a Resource cannot be Invited/Declined; an RSVP role cannot be Confirmed).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="participant"/> is null.</exception>
    /// <exception cref="ArgumentException">The <paramref name="status"/> is illegal for the <paramref name="role"/>.</exception>
    public static CalendarParticipation Create(
        ParticipantRef participant,
        ParticipationRole role,
        ParticipationStatus status)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!IsStatusLegalFor(role, status))
            throw new ArgumentException(
                $"Status '{status}' is not legal for role '{role}'. "
                + "RSVP roles (Organizer/Owner/Attendee) use Invited/Accepted/Declined/Tentative; "
                + "a Resource uses Tentative/Confirmed (a resource is reserved, not invited — it "
                + "cannot be Invited or Declined).",
                nameof(status));
        return new CalendarParticipation(participant, role, status);
    }

    /// <summary>
    /// Convenience: an <see cref="ParticipationRole.Attendee"/> participation starting at
    /// <see cref="ParticipationStatus.Invited"/> (the typical "invite the patient" entry).
    /// </summary>
    public static CalendarParticipation Attendee(ParticipantRef participant)
        => Create(participant, ParticipationRole.Attendee, ParticipationStatus.Invited);

    /// <summary>
    /// Convenience: a <see cref="ParticipationRole.Resource"/> participation. Defaults to
    /// <see cref="ParticipationStatus.Confirmed"/> (a booked resource); pass
    /// <see cref="ParticipationStatus.Tentative"/> for a held-but-unconfirmed slot.
    /// </summary>
    public static CalendarParticipation Resource(
        ParticipantRef participant,
        ParticipationStatus status = ParticipationStatus.Confirmed)
        => Create(participant, ParticipationRole.Resource, status);

    /// <summary>
    /// Convenience: an <see cref="ParticipationRole.Organizer"/> participation, defaulting to
    /// <see cref="ParticipationStatus.Accepted"/> (the organizer is implicitly attending).
    /// </summary>
    public static CalendarParticipation Organizer(
        ParticipantRef participant,
        ParticipationStatus status = ParticipationStatus.Accepted)
        => Create(participant, ParticipationRole.Organizer, status);

    /// <summary>
    /// Produce the next state for the same participant + role with a new <paramref name="status"/>
    /// (an RSVP transition, or a resource Tentative→Confirmed), re-validating the role↔status
    /// invariant.
    /// </summary>
    /// <exception cref="ArgumentException">The new <paramref name="status"/> is illegal for the role.</exception>
    public CalendarParticipation WithStatus(ParticipationStatus status)
        => Create(Participant, Role, status);

    /// <summary>
    /// Whether <paramref name="status"/> is a legal state for <paramref name="role"/> — the
    /// role↔status invariant in one place.
    /// </summary>
    public static bool IsStatusLegalFor(ParticipationRole role, ParticipationStatus status)
        => role == ParticipationRole.Resource
            // A resource is reserved, not invited: held (Tentative) or booked (Confirmed) only.
            ? status is ParticipationStatus.Tentative or ParticipationStatus.Confirmed
            // RSVP roles: the PARTSTAT states. Confirmed is the resource-booking state, not an RSVP.
            : status is ParticipationStatus.Invited
                or ParticipationStatus.Accepted
                or ParticipationStatus.Declined
                or ParticipationStatus.Tentative;
}
