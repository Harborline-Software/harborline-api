using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>Opaque identifier for a <see cref="MatchLink"/>.</summary>
[JsonConverter(typeof(MatchLinkIdJsonConverter))]
public readonly record struct MatchLinkId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator MatchLinkId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(MatchLinkId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static MatchLinkId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class MatchLinkIdJsonConverter : JsonConverter<MatchLinkId>
{
    public override MatchLinkId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("MatchLinkId must be a non-null string.");
        return new MatchLinkId(str);
    }

    public override void Write(Utf8JsonWriter writer, MatchLinkId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
