using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A calendar event — either a single (non-recurring) event or the <i>series master</i>
/// of a recurring event. The temporal core of the schedule feature (Slice S0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Series vs. occurrence (capability-and-workflow-architecture.md §7.1).</b> A recurring
/// event is a <i>definition</i> — the master carries the <see cref="Rrule"/>; occurrences are
/// generated on demand by <c>ICalendarEventExpansionService</c>, never pre-materialized. The
/// master owns three pieces of occurrence-level state:
/// <list type="bullet">
///   <item><description><b>EXDATE</b> — <see cref="ExceptionDates"/>: cancelled occurrences
///   (they disappear from the expansion). "Cancel this occurrence."</description></item>
///   <item><description><b>RECURRENCE-ID overrides</b> — <see cref="Overrides"/>: single
///   modified occurrences. "Edit this occurrence."</description></item>
///   <item><description><b>UNTIL-split / master edit</b> — <see cref="SetRrule"/> +
///   <see cref="EndSeriesOn"/> support "edit this-and-future" (UNTIL the old rule on the master,
///   caller creates a new series) and "edit all" (edit the master).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Time-of-day + timezone/DST (Slice S1).</b> The recurrence anchor and span stay
/// <see cref="DateOnly"/> (<see cref="Start"/>/<see cref="End"/>) — the RRULE generates by date —
/// while <see cref="StartTime"/>/<see cref="EndTime"/> (<see cref="TimeOnly"/>) carry the wall-clock
/// time-of-day interpreted in <see cref="Timezone"/> (an IANA id, now <i>applied</i>, no longer
/// merely carried). A date-granular / all-day event is identified by the explicit
/// <see cref="AllDay"/> flag; its <c>00:00</c> times are unused compatibility values for S0.
/// The date-granular <c>ICalendarEventExpansionService.Expand</c> is
/// unchanged; the new <c>ExpandInstants</c> resolves each occurrence to a UTC
/// <see cref="DateTimeOffset"/> pair with DST applied (a recurring 9 a.m. keeps its 9 a.m.
/// wall-clock across a spring-forward / fall-back boundary; its UTC instant shifts by an hour).
/// </para>
/// <para>
/// <b>Extension seams (Slice S2+), shaped but NOT populated in S0.</b> The entity is designed so
/// the participation/availability/coordination model bolts on without a rewrite:
/// <list type="bullet">
///   <item><description><see cref="CalendarId"/> — the owning calendar / owner ref.</description></item>
///   <item><description><see cref="Participations"/> — party/asset + role + status set
///   (the §2.8.1 meeting↔participant tie).</description></item>
///   <item><description><see cref="ResourceRef"/> / <see cref="Location"/> — the resource /
///   location ref (room, asset, place).</description></item>
/// </list>
/// In S0 these are always null/empty; the create factory does not accept them. Later slices add
/// the mutators + the wiring.
/// </para>
/// </remarks>
public sealed class CalendarEvent
{
    private readonly SortedSet<DateOnly> _exceptionDates = new();
    private readonly Dictionary<DateOnly, OccurrenceOverride> _overrides = new();

    // ---- Identity + tenancy ------------------------------------------------

    public CalendarEventId Id { get; private set; }
    public TenantId TenantId { get; private set; }

    // ---- Temporal core (Slice S0) -----------------------------------------

    public string Title { get; private set; }
    public string? Description { get; private set; }

    /// <summary>
    /// Whether this event occupies civil dates rather than wall-clock instants. This flag is the
    /// authority for all-day semantics; midnight times are never used to infer it.
    /// </summary>
    public bool AllDay { get; private set; }

    /// <summary>The event's start date. For a series, this is the recurrence anchor (DTSTART).</summary>
    public DateOnly Start { get; private set; }

    /// <summary>The event's end date (inclusive, date-granular). For a multi-day event, after <see cref="Start"/>.</summary>
    public DateOnly End { get; private set; }

    /// <summary>
    /// Wall-clock start time-of-day (Slice S1), interpreted in <see cref="Timezone"/>. A timed
    /// event carries its explicit wall-clock value. An <see cref="AllDay"/> event stores
    /// <c>00:00</c> only as an unused compatibility value; the explicit flag and civil
    /// <see cref="Start"/>/<see cref="End"/> dates are authoritative. Applied by
    /// <c>ICalendarEventExpansionService.ExpandInstants</c> with DST.
    /// </summary>
    public TimeOnly StartTime { get; private set; }

    /// <summary>
    /// Wall-clock end time-of-day (Slice S1), interpreted in <see cref="Timezone"/>. Default
    /// <c>00:00</c>. For a same-day timed event, after <see cref="StartTime"/>; the event's full
    /// span is <c>[Start+StartTime, End+EndTime]</c> in local wall-clock.
    /// </summary>
    public TimeOnly EndTime { get; private set; }

    /// <summary>RFC 5545 RRULE (e.g. <c>FREQ=WEEKLY;BYDAY=MO</c>) when this is a recurring series; <see langword="null"/> for a single event.</summary>
    public string? Rrule { get; private set; }

    /// <summary>IANA timezone id (e.g. <c>America/Los_Angeles</c>). Carried now; applied in Slice S1 (S0 is date-granular).</summary>
    public string Timezone { get; private set; }

    public CalendarEventStatus Status { get; private set; }

    /// <summary>True when this event carries an <see cref="Rrule"/> (a recurring series master).</summary>
    public bool IsRecurring => !string.IsNullOrWhiteSpace(Rrule);

    /// <summary>
    /// EXDATE set — occurrence dates that are cancelled and excluded from the expansion.
    /// Empty for a single event. Read-only; mutate via <see cref="CancelOccurrence"/> /
    /// <see cref="RestoreOccurrence"/>.
    /// </summary>
    public IReadOnlyCollection<DateOnly> ExceptionDates => _exceptionDates;

    /// <summary>
    /// RECURRENCE-ID overrides keyed by the original occurrence date. Read-only; mutate via
    /// <see cref="OverrideOccurrence"/> / <see cref="ClearOverride"/>.
    /// </summary>
    public IReadOnlyDictionary<DateOnly, OccurrenceOverride> Overrides => _overrides;

    // ---- Participation / resource model (Slice S2) ------------------------

    /// <summary>
    /// The owning calendar / owner ref. Populated in S2 (was a null seam in S0/S1); set via
    /// <see cref="SetCalendarId"/>. Optional — an event may be standalone (its participants' own
    /// calendars are the view; see <c>ICalendarParticipantCalendarQuery</c>).
    /// </summary>
    public CalendarId? CalendarId { get; private set; }

    /// <summary>
    /// Participants — each a Party or an Asset with a <see cref="ParticipationRole"/> and a
    /// <see cref="ParticipationStatus"/> (Slice S2; the §2.8.1 meeting↔participant tie). Read-only;
    /// mutate via <see cref="AddParticipation"/> / <see cref="RemoveParticipation"/> /
    /// <see cref="SetParticipationStatus"/>. The event's attendees ARE the Communication-model
    /// participants (the actual Conversation join is a later slice).
    /// </summary>
    public IReadOnlyList<CalendarParticipation> Participations => _participations;
    private readonly List<CalendarParticipation> _participations = new();

    /// <summary>
    /// The primary booked resource (a Party — a doctor's time — or an Asset — a room), when the
    /// event has a single headline resource. Populated in S2 (was an opaque <c>Guid?</c> seam in
    /// S0/S1, now the typed <see cref="ParticipantRef"/> the S0 doc anticipated). Set via
    /// <see cref="SetResource"/>; setting it also ensures a matching
    /// <see cref="ParticipationRole.Resource"/> participation exists. <see langword="null"/> for an
    /// event with no single headline resource (e.g. a multi-resource booking — every resource is in
    /// <see cref="Participations"/> as a <see cref="ParticipationRole.Resource"/>).
    /// </summary>
    public ParticipantRef? ResourceRef { get; private set; }

    /// <summary>A free-text or place ref for where the event happens (e.g. a room name, an address). Optional; set via <see cref="SetLocation"/>.</summary>
    public string? Location { get; private set; }

    // ---- Occupancy + context (Slice S3) -----------------------------------

    /// <summary>
    /// How this event occupies its resource's time (Slice S3) — the <b>common</b> occupancy
    /// classification free/busy needs. <see cref="Occupancy.Bookable"/> (an appointment that consumes
    /// availability) · <see cref="Occupancy.Blocking"/> (busy-but-not-bookable — a lunch / admin
    /// block) · <see cref="Occupancy.Tentative"/> (a held slot). Defaults to
    /// <see cref="Occupancy.Bookable"/> (the typical appointment). Free/busy subtracts ALL occupancy
    /// (Bookable AND Blocking AND Tentative) from availability. This is <i>not</i> the vertical
    /// billable / productive classification (deferred). Set via <see cref="SetOccupancy"/>.
    /// </summary>
    public Occupancy Occupancy { get; private set; }

    /// <summary>
    /// The <b>context</b> this event is scheduled <i>against</i> (Slice S3) — a thin, opaque
    /// <see cref="ContextRef"/> (a position / floor / project / case). The calendar core's
    /// coverage-support seam: it stores and exposes "what is this scheduled against" so a future
    /// coverage / rostering overlay (Pattern C) can ask "what is scheduled against this context"
    /// (<c>EventsForContext</c>) — but the core computes <b>nothing</b> about coverage. Optional;
    /// <see langword="null"/> for an event with no context anchor. Set via
    /// <see cref="SetScheduledAgainst"/>.
    /// </summary>
    public ContextRef? ScheduledAgainst { get; private set; }

    // ---- Time-footprint padding (padding slice) ---------------------------

    /// <summary>
    /// The event's <b>padding envelope</b> (the padding slice) — the pre/post durations that separate
    /// the <i>visible</i> (booked / shown) interval from the <i>occupied</i> (resource-blocking)
    /// interval. The patient sees <c>[Start..End]</c>; the resource is blocked over
    /// <c>[Start − Pre, End + Post]</c>. Defaults to <see cref="EventPadding.None"/> (0 pre + 0 post —
    /// backward-compatible: an unpadded event occupies exactly its visible span). Set via
    /// <see cref="SetPadding"/> (a per-event override); the booking path seeds it from the configured
    /// <see cref="Services.IPaddingPolicy"/> default. <b>Free/busy + no-double-book reason about the
    /// occupied interval</b> (the litmus for a first-class common field), computed in UTC from
    /// <see cref="OccurrenceInstant.OccupiedInterval"/>.
    /// </summary>
    public EventPadding Padding { get; private set; } = EventPadding.None;

    // ---- Detail visibility (Slice CALENDAR-LAYERS) ------------------------

    /// <summary>
    /// How much of this event another principal may see (Slice CALENDAR-LAYERS, demand-side
    /// visibility). <see cref="EventVisibility.Public"/> (the default — detail visible to anyone who
    /// can see the resource's calendar) · <see cref="EventVisibility.Private"/> (a personal
    /// appointment — the slot shows BUSY to others, but the detail is redacted for non-owners). The
    /// slot still occupies the resource and blocks a competing booking regardless of visibility;
    /// visibility only governs <i>detail</i> in another principal's view, not whether the time is
    /// busy. Set via <see cref="SetVisibility"/>.
    /// </summary>
    public EventVisibility Visibility { get; private set; }

    /// <summary>
    /// The owner principal for a <see cref="EventVisibility.Private"/> event — the actor whose
    /// view shows the full detail (the default <c>OwnerOnlyEventDetailVisibilityPolicy</c> reveals a
    /// private event's detail only to this actor). Defaults to <see cref="CreatedBy"/> (the creator
    /// owns their personal appointment) until set explicitly via <see cref="SetVisibility"/>.
    /// Irrelevant for a <see cref="EventVisibility.Public"/> event (its detail is always visible).
    /// </summary>
    public Guid OwnerActorId { get; private set; }

    // ---- Audit -------------------------------------------------------------

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public long Version { get; private set; }

    // ---- Construction ------------------------------------------------------

    private CalendarEvent(
        CalendarEventId id,
        TenantId tenantId,
        string title,
        DateOnly start,
        DateOnly end,
        bool allDay,
        TimeOnly startTime,
        TimeOnly endTime,
        string? rrule,
        string timezone,
        DateTimeOffset createdAt,
        Guid createdBy)
    {
        Id        = id;
        TenantId  = tenantId;
        Title     = title;
        AllDay    = allDay;
        Start     = start;
        End       = end;
        StartTime = startTime;
        EndTime   = endTime;
        Rrule     = rrule;
        Timezone  = timezone;
        Status    = CalendarEventStatus.Confirmed;
        Occupancy = Occupancy.Bookable;   // S3: default occupancy = a bookable appointment
        Visibility   = EventVisibility.Public;   // CALENDAR-LAYERS: default = publicly-detailed
        OwnerActorId = createdBy;                 // the creator owns their (private) appointment by default
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        CreatedBy = createdBy;
        UpdatedBy = createdBy;
        Version   = 0;
    }

    /// <summary>
    /// Create a new <see cref="CalendarEvent"/> — a single event when <paramref name="rrule"/>
    /// is <see langword="null"/>, or a recurring series master when it is set. Slice S0 populates
    /// only the temporal core; the S2+ extension fields (calendar/owner, participations, resource)
    /// are not accepted here.
    /// </summary>
    public static CalendarEvent Create(
        TenantId tenantId,
        string title,
        DateOnly start,
        DateOnly end,
        Guid createdBy,
        string? rrule = null,
        string timezone = "UTC",
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        Occupancy occupancy = Occupancy.Bookable,
        EventPadding? padding = null,
        DateTimeOffset? createdAt = null,
        bool allDay = false)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title must be non-empty.", nameof(title));
        if (end < start)
            throw new ArgumentException("End must be on or after Start.", nameof(end));
        if (string.IsNullOrWhiteSpace(timezone))
            throw new ArgumentException("Timezone must be non-empty (IANA tz id).", nameof(timezone));
        if (rrule is not null && string.IsNullOrWhiteSpace(rrule))
            throw new ArgumentException("Rrule, when provided, must be non-empty.", nameof(rrule));

        if (allDay && (startTime is not null || endTime is not null))
            throw new ArgumentException(
                "All-day events use civil dates and cannot carry wall-clock start/end times.",
                nameof(startTime));

        var st = startTime ?? TimeOnly.MinValue;
        var et = endTime ?? TimeOnly.MinValue;
        // For a same-day timed event the end-time must not precede the start-time (a multi-day event
        // is bounded by the dates, so cross-day times are fine).
        if (start == end && et < st)
            throw new ArgumentException("For a single-day event, EndTime must be on or after StartTime.", nameof(endTime));

        var ev = new CalendarEvent(
            id:        CalendarEventId.NewId(),
            tenantId:  tenantId,
            title:     title,
            start:     start,
            end:       end,
            allDay:    allDay,
            startTime: st,
            endTime:   et,
            rrule:     rrule,
            timezone:  timezone,
            createdAt: createdAt ?? DateTimeOffset.UtcNow,
            createdBy: createdBy);
        ev.Occupancy = occupancy;
        // Validate the padding (non-negative pre/post) via Of when a value is supplied; default = None.
        ev.Padding = padding is { } p ? EventPadding.Of(p.Pre, p.Post) : EventPadding.None;
        return ev;
    }

    // ---- Master edit ("edit all") -----------------------------------------

    /// <summary>
    /// Edit the series master's core fields ("edit all"). Applies to every non-overridden occurrence.
    /// Optionally also sets the wall-clock <see cref="StartTime"/>/<see cref="EndTime"/> (Slice S1);
    /// pass <see langword="null"/> to leave the times unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anchor-move guard (capability-and-workflow-architecture.md §7.1 "known edge"; deep-review
    /// M1).</b> <see cref="Start"/> is the DTSTART the recurrence rule walks from. Moving it
    /// regenerates a <i>different</i> occurrence-date set, which would strand every EXDATE / override
    /// keyed to an old generated date — they would <b>silently vanish</b> from the expansion. §7.1
    /// forbids that ("never silently clobber"). So this method <b>rejects</b> an anchor move while any
    /// occurrence edit (EXDATE or RECURRENCE-ID override) exists: clear or re-key the occurrence edits
    /// first (<see cref="RestoreOccurrence"/> / <see cref="ClearOverride"/>), then move the anchor.
    /// A same-anchor "edit all" (rename, retime, change the end-date without moving Start) is always
    /// allowed and preserves every override per the §7.1 "detached occurrence is preserved" rule.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="start"/> differs from the current <see cref="Start"/> while
    /// occurrence-level edits (EXDATE or override) exist — the anchor-move-with-overrides edge.
    /// </exception>
    public void EditMaster(
        string title,
        DateOnly start,
        DateOnly end,
        Guid updatedBy,
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        DateTimeOffset? updatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title must be non-empty.", nameof(title));
        if (end < start)
            throw new ArgumentException("End must be on or after Start.", nameof(end));

        // M1 / §7.1 known edge: reject an anchor move while occurrence edits exist (never silently
        // strand an EXDATE / override keyed to an old generated date).
        if (start != Start && (_exceptionDates.Count > 0 || _overrides.Count > 0))
            throw new InvalidOperationException(
                "Cannot move the series anchor (Start) while occurrence edits exist: moving the "
                + "anchor would silently strand the EXDATE(s)/override(s) keyed to old occurrence "
                + "dates (capability-and-workflow-architecture.md §7.1 — never silently clobber). "
                + "Restore the cancelled occurrences / clear the overrides first, then move the anchor.");

        var st = startTime ?? StartTime;
        var et = endTime ?? EndTime;
        if (start == end && et < st)
            throw new ArgumentException("For a single-day event, EndTime must be on or after StartTime.", nameof(endTime));

        Title     = title;
        Start     = start;
        End       = end;
        StartTime = st;
        EndTime   = et;
        Stamp(updatedBy, updatedAt);
    }

    public void SetDescription(string? description, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        Description = description;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Replace the recurrence rule on the master. Used by the "edit all" path and as the first
    /// half of "edit this-and-future" (the caller sets an <c>UNTIL=</c> on the master's existing
    /// rule via <see cref="EndSeriesOn"/>, then creates a new series for the future). Pass
    /// <see langword="null"/> to make this a single (non-recurring) event.
    /// </summary>
    public void SetRrule(string? rrule, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        if (rrule is not null && string.IsNullOrWhiteSpace(rrule))
            throw new ArgumentException("Rrule, when provided, must be non-empty.", nameof(rrule));
        Rrule = rrule;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// "Edit this-and-future" split, master half: bound the existing series at
    /// <paramref name="lastDate"/> by appending <c>UNTIL=</c> to the RRULE (never rewriting the
    /// past). The caller then creates a NEW <see cref="CalendarEvent"/> series starting the day
    /// after <paramref name="lastDate"/> with the edited fields. No-op-safe: appends/replaces the
    /// UNTIL component idempotently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zombie-override prune (deep-review M1 / §7.1).</b> Truncating the series at
    /// <paramref name="lastDate"/> means occurrences <i>after</i> it no longer exist on this master
    /// (they belong to the caller's new future series). Any EXDATE or RECURRENCE-ID override whose
    /// key (its original occurrence date) is strictly past <paramref name="lastDate"/> would
    /// otherwise be a <i>zombie</i>: a detached override whose RECURRENCE-ID slot the truncated rule
    /// no longer generates, yet which the expander's "moved-into-window" second pass could still
    /// surface — a phantom occurrence past the series end. To keep the truncation semantically
    /// complete and avoid silently surfacing a stale override, those past-UNTIL occurrence edits are
    /// <b>pruned here</b> and the count is returned so the caller can carry them onto the new future
    /// series if intended (§7.1 "preserve or prompt; never silently clobber" — the prune is surfaced,
    /// not silent).
    /// </para>
    /// </remarks>
    /// <returns>The number of occurrence edits (EXDATEs + overrides) past <paramref name="lastDate"/> that were pruned.</returns>
    /// <exception cref="InvalidOperationException">Thrown when this event is not a recurring series.</exception>
    public int EndSeriesOn(DateOnly lastDate, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        if (!IsRecurring)
            throw new InvalidOperationException("EndSeriesOn applies only to a recurring series (no RRULE present).");
        if (lastDate < Start)
            throw new ArgumentException("UNTIL date must be on or after the series anchor (Start).", nameof(lastDate));

        Rrule = ApplyUntil(Rrule!, lastDate);

        // Prune zombie occurrence edits whose RECURRENCE-ID is past the new series end.
        var prunedExdates = _exceptionDates.Where(d => d > lastDate).ToList();
        foreach (var d in prunedExdates) _exceptionDates.Remove(d);

        var prunedOverrideKeys = _overrides.Keys.Where(k => k > lastDate).ToList();
        foreach (var k in prunedOverrideKeys) _overrides.Remove(k);

        Stamp(updatedBy, updatedAt);
        return prunedExdates.Count + prunedOverrideKeys.Count;
    }

    // ---- Cancel this occurrence (EXDATE) ----------------------------------

    /// <summary>
    /// Cancel a single occurrence by its date (EXDATE). It disappears from the expansion; the
    /// series is otherwise unchanged. If an override exists for that date it is removed too
    /// (the occurrence is gone, not merely detached). Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this event is not a recurring series.</exception>
    public void CancelOccurrence(DateOnly occurrenceDate, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        if (!IsRecurring)
            throw new InvalidOperationException("CancelOccurrence applies only to a recurring series.");

        bool changed = _exceptionDates.Add(occurrenceDate);
        changed |= _overrides.Remove(occurrenceDate);
        if (changed) Stamp(updatedBy, updatedAt);
    }

    /// <summary>Un-cancel a previously EXDATE'd occurrence. Idempotent.</summary>
    /// <exception cref="InvalidOperationException">Thrown when this event is not a recurring series.</exception>
    public void RestoreOccurrence(DateOnly occurrenceDate, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        if (!IsRecurring)
            throw new InvalidOperationException("RestoreOccurrence applies only to a recurring series.");
        if (_exceptionDates.Remove(occurrenceDate)) Stamp(updatedBy, updatedAt);
    }

    // ---- Edit this occurrence (RECURRENCE-ID override) --------------------

    /// <summary>
    /// Edit a single occurrence (RECURRENCE-ID override). The occurrence at
    /// <see cref="OccurrenceOverride.RecurrenceId"/> is replaced by the override's values in the
    /// expansion; the series master and every other occurrence are unchanged. Re-overriding the
    /// same RECURRENCE-ID replaces the prior override (last-edit-wins on that one occurrence).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this event is not a recurring series, or the target occurrence is EXDATE-cancelled.</exception>
    public void OverrideOccurrence(OccurrenceOverride @override, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(@override);
        if (!IsRecurring)
            throw new InvalidOperationException("OverrideOccurrence applies only to a recurring series.");
        if (_exceptionDates.Contains(@override.RecurrenceId))
            throw new InvalidOperationException(
                $"Occurrence {@override.RecurrenceId:O} is cancelled (EXDATE); restore it before overriding.");
        if (@override.NewEnd is { } ne && ne < @override.NewStart)
            throw new ArgumentException("Override NewEnd must be on or after NewStart.", nameof(@override));

        _overrides[@override.RecurrenceId] = @override;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>Remove an occurrence override, restoring it to the series-generated occurrence. Idempotent.</summary>
    public void ClearOverride(DateOnly recurrenceId, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        if (_overrides.Remove(recurrenceId)) Stamp(updatedBy, updatedAt);
    }

    // ---- Status ------------------------------------------------------------

    /// <summary>Cancel the whole event / series (RFC 5545 STATUS:CANCELLED). To cancel a single occurrence, use <see cref="CancelOccurrence"/>.</summary>
    public void Cancel(Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        Status = CalendarEventStatus.Cancelled;
        Stamp(updatedBy, updatedAt);
    }

    public void SetTentative(Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        Status = CalendarEventStatus.Tentative;
        Stamp(updatedBy, updatedAt);
    }

    public void Confirm(Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        Status = CalendarEventStatus.Confirmed;
        Stamp(updatedBy, updatedAt);
    }

    // ---- Participation / resource model (Slice S2) ------------------------

    /// <summary>Set (or clear, with <see langword="null"/>) the owning calendar / owner ref.</summary>
    public void SetCalendarId(CalendarId? calendarId, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        CalendarId = calendarId;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>Set (or clear, with <see langword="null"/>) the free-text location / place ref.</summary>
    public void SetLocation(string? location, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        Location = string.IsNullOrWhiteSpace(location) ? null : location;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Set the primary booked resource (a Party — a doctor — or an Asset — a room). Also ensures a
    /// matching <see cref="ParticipationRole.Resource"/> participation exists for it (so the
    /// resource shows on its own calendar via <c>EventsFor</c> and the participation set is the
    /// single source of truth). The participation defaults to <paramref name="status"/>
    /// (<see cref="ParticipationStatus.Confirmed"/> by default — a booked resource). Re-setting to a
    /// different resource leaves any prior resource participation in place (remove it explicitly with
    /// <see cref="RemoveParticipation"/> if it should no longer attend).
    /// </summary>
    public void SetResource(
        ParticipantRef resource,
        Guid updatedBy,
        ParticipationStatus status = ParticipationStatus.Confirmed,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ResourceRef = resource;
        // Keep the participation set authoritative: ensure the headline resource is a Resource
        // participant (idempotent on participant+role).
        UpsertParticipationCore(CalendarParticipation.Resource(resource, status));
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>Clear the primary resource ref. Does NOT remove the Resource participation (remove it explicitly if intended).</summary>
    public void ClearResource(Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        ResourceRef = null;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Set the <see cref="Occupancy"/> classification (Slice S3) — flip an event between an
    /// appointment (<see cref="Occupancy.Bookable"/>), an internal block
    /// (<see cref="Occupancy.Blocking"/> — a lunch/admin block), and a held slot
    /// (<see cref="Occupancy.Tentative"/>). Free/busy subtracts all three as occupancy.
    /// </summary>
    public void SetOccupancy(Occupancy occupancy, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        Occupancy = occupancy;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Set (or clear, with <see langword="null"/>) the <see cref="ScheduledAgainst"/> context ref
    /// (Slice S3) — anchor this event against a position / floor / project / case. The coverage seam:
    /// the core stores it and exposes it via the by-context query; it never interprets it.
    /// </summary>
    public void SetScheduledAgainst(ContextRef? context, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        ScheduledAgainst = context;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Set the <see cref="Padding"/> envelope (the padding slice) — a <b>per-event override</b> of the
    /// configured policy default. Pre/post must be non-negative (validated via
    /// <see cref="EventPadding.Of"/>). Pass <see cref="EventPadding.None"/> to clear padding back to the
    /// backward-compatible "occupied == visible" behavior. Free/busy + no-double-book read the new value
    /// on the next query (the occupied interval widens to <c>[Start − Pre, End + Post]</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Either padding duration is negative.</exception>
    public void SetPadding(EventPadding padding, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        Padding = EventPadding.Of(padding.Pre, padding.Post); // re-validate non-negativity
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Set the detail-<see cref="Visibility"/> of this event (Slice CALENDAR-LAYERS) — mark it
    /// <see cref="EventVisibility.Private"/> (a personal appointment whose detail is redacted for
    /// non-owners; it still shows BUSY) or back to <see cref="EventVisibility.Public"/>. When making
    /// it private, <paramref name="ownerActorId"/> sets the principal whose own view shows the detail;
    /// pass <see langword="null"/> to keep the current owner (which defaults to the creator). The
    /// authorization decision for non-owners is the host-registered
    /// <see cref="Services.IEventDetailVisibilityPolicy"/>'s job; this only records the classification
    /// + owner.
    /// </summary>
    public void SetVisibility(EventVisibility visibility, Guid updatedBy, Guid? ownerActorId = null, DateTimeOffset? updatedAt = null)
    {
        Visibility = visibility;
        if (ownerActorId is { } owner) OwnerActorId = owner;
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Add (or replace, on the same participant + role) a participation. Identity is
    /// (<see cref="CalendarParticipation.Participant"/>, <see cref="CalendarParticipation.Role"/>):
    /// the same Party may appear once as an Attendee and once as a Resource, but adding the same
    /// participant+role twice replaces the prior entry (last-write-wins on status). The role↔status
    /// invariant was already enforced when the <paramref name="participation"/> was constructed.
    /// </summary>
    public void AddParticipation(CalendarParticipation participation, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(participation);
        UpsertParticipationCore(participation);
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>
    /// Remove a participant in a given role. Idempotent; returns true when a participation was
    /// removed. If the removed participation was the primary <see cref="ResourceRef"/>, the resource
    /// ref is cleared too (the participation set stays authoritative).
    /// </summary>
    public bool RemoveParticipation(ParticipantRef participant, ParticipationRole role, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        var idx = _participations.FindIndex(p => p.Participant == participant && p.Role == role);
        if (idx < 0) return false;
        _participations.RemoveAt(idx);
        if (role == ParticipationRole.Resource && ResourceRef == participant)
            ResourceRef = null;
        Stamp(updatedBy, updatedAt);
        return true;
    }

    /// <summary>
    /// Transition a participant's status in a given role (an RSVP accept/decline, or a resource
    /// Tentative→Confirmed). Re-validates the role↔status invariant via
    /// <see cref="CalendarParticipation.WithStatus"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No participation exists for that participant + role.</exception>
    /// <exception cref="ArgumentException">The new <paramref name="status"/> is illegal for the role.</exception>
    public void SetParticipationStatus(
        ParticipantRef participant,
        ParticipationRole role,
        ParticipationStatus status,
        Guid updatedBy,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        var idx = _participations.FindIndex(p => p.Participant == participant && p.Role == role);
        if (idx < 0)
            throw new InvalidOperationException(
                $"No {role} participation for participant {participant} exists on this event.");
        _participations[idx] = _participations[idx].WithStatus(status); // re-validates the invariant
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>Insert-or-replace a participation by (participant, role) without stamping — the shared core for the S2 mutators and rehydration.</summary>
    private void UpsertParticipationCore(CalendarParticipation participation)
    {
        var idx = _participations.FindIndex(
            p => p.Participant == participation.Participant && p.Role == participation.Role);
        if (idx >= 0) _participations[idx] = participation;
        else _participations.Add(participation);
    }

    // ---- Rehydration (store round-trip) -----------------------------------

    /// <summary>
    /// Rehydrate a <see cref="CalendarEvent"/> from persisted state — used by the store to
    /// reconstruct an entity (including its EXDATE set + overrides + audit) without re-running
    /// the create/edit factories. The store is the only caller (the override store round-trips
    /// through this).
    /// </summary>
    public static CalendarEvent Rehydrate(
        CalendarEventId id,
        TenantId tenantId,
        string title,
        string? description,
        DateOnly start,
        DateOnly end,
        TimeOnly startTime,
        TimeOnly endTime,
        string? rrule,
        string timezone,
        CalendarEventStatus status,
        IEnumerable<DateOnly> exceptionDates,
        IEnumerable<OccurrenceOverride> overrides,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        Guid createdBy,
        Guid updatedBy,
        long version,
        bool allDay = false,
        CalendarId? calendarId = null,
        IEnumerable<CalendarParticipation>? participations = null,
        ParticipantRef? resourceRef = null,
        string? location = null,
        Occupancy occupancy = Occupancy.Bookable,
        ContextRef? scheduledAgainst = null,
        EventPadding? padding = null,
        EventVisibility visibility = EventVisibility.Public,
        Guid? ownerActorId = null)
    {
        var ev = new CalendarEvent(
            id, tenantId, title, start, end, allDay, startTime, endTime, rrule, timezone, createdAt, createdBy)
        {
            Description      = description,
            Status           = status,
            UpdatedAt        = updatedAt,
            UpdatedBy        = updatedBy,
            Version          = version,
            CalendarId       = calendarId,
            ResourceRef      = resourceRef,
            Location         = location,
            Occupancy        = occupancy,
            ScheduledAgainst = scheduledAgainst,
            Padding          = padding ?? EventPadding.None,
            Visibility       = visibility,
            OwnerActorId     = ownerActorId ?? createdBy,
        };
        foreach (var ex in exceptionDates) ev._exceptionDates.Add(ex);
        foreach (var ov in overrides) ev._overrides[ov.RecurrenceId] = ov;
        if (participations is not null)
            foreach (var p in participations) ev.UpsertParticipationCore(p);
        return ev;
    }

    // ---- Helpers -----------------------------------------------------------

    private void Stamp(Guid updatedBy, DateTimeOffset? updatedAt)
    {
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
        Version  += 1;
    }

    /// <summary>
    /// Append or replace the <c>UNTIL=</c> component of an RRULE with <paramref name="until"/>
    /// (RFC 5545 date form <c>YYYYMMDD</c>). Removes any existing <c>COUNT=</c> (UNTIL and COUNT
    /// are mutually exclusive) and any prior <c>UNTIL=</c>. The shared expander already lets
    /// UNTIL win over COUNT, but we normalize here so the persisted rule is unambiguous.
    /// </summary>
    internal static string ApplyUntil(string rrule, DateOnly until)
    {
        var kept = rrule
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t =>
            {
                var key = t.Split('=', 2)[0].Trim().ToUpperInvariant();
                return key is not ("UNTIL" or "COUNT");
            });

        var untilToken = $"UNTIL={until:yyyyMMdd}";
        return string.Join(';', kept.Append(untilToken));
    }
}
