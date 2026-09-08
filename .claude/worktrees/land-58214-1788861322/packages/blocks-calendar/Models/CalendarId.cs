using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// Strongly-typed identifier for the owning <i>calendar</i> a
/// <see cref="CalendarEvent"/> belongs to (e.g. a user's personal calendar,
/// a shared team calendar, a property calendar). UUIDv7.
/// </summary>
/// <remarks>
/// <b>Extension seam (Slice S2+).</b> The calendar/owner concept is shaped here
/// so <see cref="CalendarEvent"/> can carry a typed owner ref without a rewrite,
/// but Slice S0 (temporal core) does not populate or enforce it — every S0 event
/// has a <see langword="null"/> <see cref="CalendarEvent.CalendarId"/>. The
/// calendar entity, sharing/visibility, and per-calendar query surface land in a
/// later slice.
/// </remarks>
[JsonConverter(typeof(CalendarIdJsonConverter))]
public readonly record struct CalendarId(Guid Value)
{
    public override string ToString() => Value.ToString();
    public static CalendarId NewId() => new(Guid.CreateVersion7());
    public static implicit operator Guid(CalendarId id) => id.Value;
}

internal sealed class CalendarIdJsonConverter : JsonConverter<CalendarId>
{
    public override CalendarId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("CalendarId must be a non-null string.");
        return new CalendarId(Guid.Parse(str));
    }

    public override void Write(Utf8JsonWriter writer, CalendarId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value.ToString());
}
