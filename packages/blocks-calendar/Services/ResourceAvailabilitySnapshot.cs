using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Flat, serializable snapshot of a <see cref="ResourceAvailability"/> (Slice S3) — what round-trips
/// through <see cref="IResourceAvailabilityStore"/> (the resource ref + tz + windows + exception
/// dates). Mirrors the <see cref="CalendarEventSnapshot"/> discipline: a flat shape that genuinely
/// (de)serializes so the in-memory store exercises the real persistence path.
/// </summary>
public sealed record ResourceAvailabilitySnapshot
{
    public required string TenantId { get; init; }
    public required ParticipantRefSnapshot ResourceRef { get; init; }
    public required string Timezone { get; init; }
    public required IReadOnlyList<AvailabilityWindowSnapshot> Windows { get; init; }
    public required IReadOnlyList<DateOnly> ExceptionDates { get; init; }

    /// <summary>
    /// The spanning per-resource vacation / closure exceptions (Slice CALENDAR-LAYERS). Not
    /// <c>required</c> so an older S3 snapshot (no spanning exceptions) deserializes to an empty list.
    /// </summary>
    public IReadOnlyList<ExceptionSpanSnapshot> ExceptionSpans { get; init; } = Array.Empty<ExceptionSpanSnapshot>();

    public static ResourceAvailabilitySnapshot FromEntity(ResourceAvailability ra)
    {
        ArgumentNullException.ThrowIfNull(ra);
        return new ResourceAvailabilitySnapshot
        {
            TenantId       = ra.TenantId.Value,
            ResourceRef    = ParticipantRefSnapshot.FromModel(ra.ResourceRef)!,
            Timezone       = ra.Timezone,
            Windows        = ra.Windows.Select(AvailabilityWindowSnapshot.FromModel).ToList(),
            ExceptionDates = ra.ExceptionDates.ToList(),
            ExceptionSpans = ra.ExceptionSpans.Select(ExceptionSpanSnapshot.FromModel).ToList(),
        };
    }

    public ResourceAvailability ToEntity()
        => ResourceAvailability.Rehydrate(
            tenantId:       new TenantId(TenantId),
            resourceRef:    ResourceRef.ToModel(),
            timezone:       Timezone,
            windows:        Windows.Select(w => w.ToModel()),
            exceptionDates: ExceptionDates,
            exceptionSpans: ExceptionSpans.Select(s => s.ToModel()));
}

/// <summary>Flat, serializable form of an <see cref="ExceptionSpan"/> (Slice CALENDAR-LAYERS).</summary>
public sealed record ExceptionSpanSnapshot
{
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public string? Reason { get; init; }

    public static ExceptionSpanSnapshot FromModel(ExceptionSpan s)
        => new() { Start = s.Start, End = s.End, Reason = s.Reason };

    public ExceptionSpan ToModel() => ExceptionSpan.Create(Start, End, Reason);
}

/// <summary>Flat, serializable form of an <see cref="AvailabilityWindow"/>.</summary>
public sealed record AvailabilityWindowSnapshot
{
    public required DateOnly AnchorDate { get; init; }
    public required TimeOnly StartTime { get; init; }
    public required TimeOnly EndTime { get; init; }
    public string? Rrule { get; init; }

    public static AvailabilityWindowSnapshot FromModel(AvailabilityWindow w)
        => new()
        {
            AnchorDate = w.AnchorDate,
            StartTime  = w.StartTime,
            EndTime    = w.EndTime,
            Rrule      = w.Rrule,
        };

    public AvailabilityWindow ToModel() => AvailabilityWindow.Create(AnchorDate, StartTime, EndTime, Rrule);
}
