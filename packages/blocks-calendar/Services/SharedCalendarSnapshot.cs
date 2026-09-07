using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Flat, serializable snapshot of a <see cref="SharedCalendar"/> (Slice CALENDAR-LAYERS) — what
/// round-trips through <see cref="ISharedCalendarStore"/> (id + tenant + name + exception spans).
/// Mirrors the <see cref="CalendarEventSnapshot"/> / <see cref="ResourceAvailabilitySnapshot"/>
/// discipline: a flat shape that genuinely (de)serializes so the in-memory store exercises the real
/// persistence path; a durable EF-backed store reuses the same shape.
/// </summary>
public sealed record SharedCalendarSnapshot
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ExceptionSpanSnapshot> Exceptions { get; init; }

    public static SharedCalendarSnapshot FromEntity(SharedCalendar c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new SharedCalendarSnapshot
        {
            Id         = c.Id.Value,
            TenantId   = c.TenantId.Value,
            Name       = c.Name,
            Exceptions = c.Exceptions.Select(ExceptionSpanSnapshot.FromModel).ToList(),
        };
    }

    public SharedCalendar ToEntity()
        => SharedCalendar.Rehydrate(
            id:         new SharedCalendarId(Id),
            tenantId:   new TenantId(TenantId),
            name:       Name,
            exceptions: Exceptions.Select(e => e.ToModel()));
}
