using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Flat, serializable snapshot of a <see cref="CalendarEvent"/> — the persisted shape of the
/// series/occurrence store (master fields + EXDATE list + RECURRENCE-ID overrides + audit). The
/// snapshot is what round-trips through the store; <see cref="ToEntity"/> rehydrates a full
/// <see cref="CalendarEvent"/> from it.
/// </summary>
/// <remarks>
/// This snapshot carries the Slice S0 temporal core + occurrence state plus the Slice S2
/// participation / resource model (calendar/owner ref, participations, resource/location). The S2
/// fields are <i>not</i> <c>required</c> so an older S0/S1 snapshot (which lacks them) still
/// deserializes — to an empty participation set / null refs, the correct backward-compatible
/// default.
/// </remarks>
public sealed record CalendarEventSnapshot
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// Explicit all-day authority. Older snapshots default to <see langword="false"/>; midnight
    /// values are deliberately not reinterpreted as all-day events.
    /// </summary>
    public bool AllDay { get; init; }

    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }

    /// <summary>
    /// Wall-clock start time-of-day (Slice S1). Not <c>required</c> so an older S0 snapshot (no
    /// time fields) deserializes to <c>00:00</c> — the correct all-day / date-granular default.
    /// </summary>
    public TimeOnly StartTime { get; init; }

    /// <summary>Wall-clock end time-of-day (Slice S1). Defaults to <c>00:00</c> for an older S0 snapshot.</summary>
    public TimeOnly EndTime { get; init; }

    public string? Rrule { get; init; }
    public required string Timezone { get; init; }
    public required CalendarEventStatus Status { get; init; }
    public required IReadOnlyList<DateOnly> ExceptionDates { get; init; }
    public required IReadOnlyList<OccurrenceOverride> Overrides { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required Guid CreatedBy { get; init; }
    public required Guid UpdatedBy { get; init; }
    public required long Version { get; init; }

    // ---- Slice S2 participation / resource model (not required → S0/S1-snapshot-compatible) ----

    /// <summary>The owning calendar / owner ref (Slice S2). <see langword="null"/> when none / for an older snapshot.</summary>
    public Guid? CalendarId { get; init; }

    /// <summary>The participants (Slice S2). Defaults to an empty list for an older S0/S1 snapshot.</summary>
    public IReadOnlyList<ParticipationSnapshot> Participations { get; init; } = Array.Empty<ParticipationSnapshot>();

    /// <summary>The primary resource ref (Slice S2). <see langword="null"/> when none / for an older snapshot.</summary>
    public ParticipantRefSnapshot? ResourceRef { get; init; }

    /// <summary>The free-text location / place (Slice S2). <see langword="null"/> when none.</summary>
    public string? Location { get; init; }

    // ---- Slice S3 occupancy + context (not required → S0/S1/S2-snapshot-compatible) ----

    /// <summary>
    /// The occupancy classification (Slice S3). Not <c>required</c> so an older snapshot deserializes
    /// to <see cref="Occupancy.Bookable"/> (= 0, the correct default for a pre-S3 appointment event).
    /// </summary>
    public Occupancy Occupancy { get; init; }

    /// <summary>The context the event is scheduled against (Slice S3). <see langword="null"/> for none / an older snapshot.</summary>
    public ContextRefSnapshot? ScheduledAgainst { get; init; }

    // ---- Padding slice (not required → S0/S1/S2/S3-snapshot-compatible) ----

    /// <summary>
    /// The pre-padding duration (the padding slice). Not <c>required</c> so an older snapshot
    /// deserializes to <see cref="TimeSpan.Zero"/> — the backward-compatible "no padding" default
    /// (occupied interval equals visible interval).
    /// </summary>
    public TimeSpan PrePadding { get; init; }

    /// <summary>The post-padding duration (the padding slice). Defaults to <see cref="TimeSpan.Zero"/> for an older snapshot.</summary>
    public TimeSpan PostPadding { get; init; }

    // ---- Slice CALENDAR-LAYERS detail visibility (not required → older-snapshot-compatible) ----

    /// <summary>
    /// The detail-visibility (Slice CALENDAR-LAYERS). Not <c>required</c> so an older snapshot
    /// deserializes to <see cref="EventVisibility.Public"/> (= 0, the correct default — a pre-layers
    /// event is publicly detailed).
    /// </summary>
    public EventVisibility Visibility { get; init; }

    /// <summary>
    /// The owner principal for a private event (Slice CALENDAR-LAYERS). <see langword="null"/> for an
    /// older snapshot — rehydration then falls back to <see cref="CreatedBy"/> (the creator owns it).
    /// </summary>
    public Guid? OwnerActorId { get; init; }

    /// <summary>Capture a <see cref="CalendarEvent"/> into a snapshot for persistence.</summary>
    public static CalendarEventSnapshot FromEntity(CalendarEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return new CalendarEventSnapshot
        {
            Id             = ev.Id.Value,
            TenantId       = ev.TenantId.Value,
            Title          = ev.Title,
            Description    = ev.Description,
            AllDay         = ev.AllDay,
            Start          = ev.Start,
            End            = ev.End,
            StartTime      = ev.StartTime,
            EndTime        = ev.EndTime,
            Rrule          = ev.Rrule,
            Timezone       = ev.Timezone,
            Status         = ev.Status,
            ExceptionDates = ev.ExceptionDates.ToList(),
            Overrides      = ev.Overrides.Values.ToList(),
            CreatedAt      = ev.CreatedAt,
            UpdatedAt      = ev.UpdatedAt,
            CreatedBy      = ev.CreatedBy,
            UpdatedBy      = ev.UpdatedBy,
            Version        = ev.Version,
            CalendarId       = ev.CalendarId?.Value,
            Participations   = ev.Participations.Select(ParticipationSnapshot.FromModel).ToList(),
            ResourceRef      = ParticipantRefSnapshot.FromModel(ev.ResourceRef),
            Location         = ev.Location,
            Occupancy        = ev.Occupancy,
            ScheduledAgainst = ContextRefSnapshot.FromModel(ev.ScheduledAgainst),
            PrePadding       = ev.Padding.Pre,
            PostPadding      = ev.Padding.Post,
            Visibility       = ev.Visibility,
            OwnerActorId     = ev.OwnerActorId,
        };
    }

    /// <summary>Rehydrate a full <see cref="CalendarEvent"/> from this snapshot.</summary>
    public CalendarEvent ToEntity()
        => CalendarEvent.Rehydrate(
            id:             new CalendarEventId(Id),
            tenantId:       new TenantId(TenantId),
            title:          Title,
            description:    Description,
            start:          Start,
            end:            End,
            startTime:      StartTime,
            endTime:        EndTime,
            rrule:          Rrule,
            timezone:       Timezone,
            status:         Status,
            exceptionDates: ExceptionDates,
            overrides:      Overrides,
            createdAt:      CreatedAt,
            updatedAt:      UpdatedAt,
            createdBy:      CreatedBy,
            updatedBy:      UpdatedBy,
            version:        Version,
            allDay:         AllDay,
            calendarId:       CalendarId is { } cid ? new CalendarId(cid) : null,
            participations:   Participations.Select(p => p.ToModel()).ToList(),
            resourceRef:      ResourceRef?.ToModel(),
            location:         Location,
            occupancy:        Occupancy,
            scheduledAgainst: ScheduledAgainst?.ToModel(),
            padding:          EventPadding.Of(PrePadding, PostPadding),
            visibility:       Visibility,
            ownerActorId:     OwnerActorId);
}

/// <summary>
/// Flat, serializable form of a <see cref="ParticipantRef"/> — the discriminated Party/Asset ref
/// stored as <c>(Kind, Value)</c> so it round-trips through JSON without polymorphic-record
/// converters (the snapshot's "flat, serializable" discipline).
/// </summary>
public sealed record ParticipantRefSnapshot
{
    public required ParticipantKind Kind { get; init; }
    public required string Value { get; init; }

    public static ParticipantRefSnapshot? FromModel(ParticipantRef? r)
        => r is null ? null : new ParticipantRefSnapshot { Kind = r.Kind, Value = r.Value };

    public ParticipantRef ToModel()
        => Kind == ParticipantKind.Party ? ParticipantRef.Party(Value) : ParticipantRef.Asset(Value);
}

/// <summary>
/// Flat, serializable form of a <see cref="ContextRef"/> — the opaque <c>(Kind, Value)</c> context
/// ref stored verbatim (the calendar never interprets either field).
/// </summary>
public sealed record ContextRefSnapshot
{
    public required string Kind { get; init; }
    public required string Value { get; init; }

    public static ContextRefSnapshot? FromModel(ContextRef? c)
        => c is null ? null : new ContextRefSnapshot { Kind = c.Kind, Value = c.Value };

    public ContextRef ToModel() => ContextRef.Of(Kind, Value);
}

/// <summary>Flat, serializable form of a <see cref="CalendarParticipation"/>.</summary>
public sealed record ParticipationSnapshot
{
    public required ParticipantRefSnapshot Participant { get; init; }
    public required ParticipationRole Role { get; init; }
    public required ParticipationStatus Status { get; init; }

    public static ParticipationSnapshot FromModel(CalendarParticipation p)
        => new()
        {
            Participant = ParticipantRefSnapshot.FromModel(p.Participant)!,
            Role        = p.Role,
            Status      = p.Status,
        };

    public CalendarParticipation ToModel()
        => CalendarParticipation.Create(Participant.ToModel(), Role, Status);
}
