using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// Strongly-typed identifier for a <see cref="SharedCalendar"/> — an org/location/group-owned
/// supply-side calendar of availability exceptions (a clinic holiday calendar) that applies to a set
/// of resources via subscriptions (Slice CALENDAR-LAYERS). UUIDv7, mirroring
/// <see cref="CalendarId"/>.
/// </summary>
[JsonConverter(typeof(SharedCalendarIdJsonConverter))]
public readonly record struct SharedCalendarId(Guid Value)
{
    public override string ToString() => Value.ToString();
    public static SharedCalendarId NewId() => new(Guid.CreateVersion7());
    public static implicit operator Guid(SharedCalendarId id) => id.Value;
}

internal sealed class SharedCalendarIdJsonConverter : JsonConverter<SharedCalendarId>
{
    public override SharedCalendarId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("SharedCalendarId must be a non-null string.");
        return new SharedCalendarId(Guid.Parse(str));
    }

    public override void Write(Utf8JsonWriter writer, SharedCalendarId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value.ToString());
}
