using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Flat, serializable snapshot of an <see cref="OwnedCalendar"/> — the persisted shape of the
/// owned-calendar store (calendar-productization design note #149, slice C1). Mirrors the
/// <see cref="CalendarEventSnapshot"/> discipline: the store round-trips this JSON document, and
/// <see cref="ToEntity"/> rehydrates a full <see cref="OwnedCalendar"/> from it. Reuses the shared
/// <see cref="ParticipantRefSnapshot"/> (defined alongside <see cref="CalendarEventSnapshot"/>).
/// </summary>
public sealed record CalendarSnapshot
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public required CalendarKind Kind { get; init; }

    /// <summary>The design-token colour name (never a hex literal). <see langword="null"/> when unassigned.</summary>
    public string? ColorToken { get; init; }

    public required Guid OwnerActorId { get; init; }

    /// <summary>The party/asset this calendar is a lens over — set only for a Resource calendar.</summary>
    public ParticipantRefSnapshot? ResourceRef { get; init; }

    public required bool IsDefault { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required Guid CreatedBy { get; init; }
    public required Guid UpdatedBy { get; init; }
    public required long Version { get; init; }

    /// <summary>Capture an <see cref="OwnedCalendar"/> into a snapshot for persistence.</summary>
    public static CalendarSnapshot FromEntity(OwnedCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return new CalendarSnapshot
        {
            Id           = calendar.Id.Value,
            TenantId     = calendar.TenantId.Value,
            Name         = calendar.Name,
            Kind         = calendar.Kind,
            ColorToken   = calendar.ColorToken,
            OwnerActorId = calendar.OwnerActorId,
            ResourceRef  = ParticipantRefSnapshot.FromModel(calendar.ResourceRef),
            IsDefault    = calendar.IsDefault,
            CreatedAt    = calendar.CreatedAt,
            UpdatedAt    = calendar.UpdatedAt,
            CreatedBy    = calendar.CreatedBy,
            UpdatedBy    = calendar.UpdatedBy,
            Version      = calendar.Version,
        };
    }

    /// <summary>Rehydrate a full <see cref="OwnedCalendar"/> from this snapshot.</summary>
    public OwnedCalendar ToEntity()
        => OwnedCalendar.Rehydrate(
            id:           new CalendarId(Id),
            tenantId:     new TenantId(TenantId),
            name:         Name,
            kind:         Kind,
            ownerActorId: OwnerActorId,
            resourceRef:  ResourceRef?.ToModel(),
            isDefault:    IsDefault,
            colorToken:   ColorToken,
            createdAt:    CreatedAt,
            updatedAt:    UpdatedAt,
            createdBy:    CreatedBy,
            updatedBy:    UpdatedBy,
            version:      Version);
}
