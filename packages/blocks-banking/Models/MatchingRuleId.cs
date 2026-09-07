using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>Opaque identifier for a <see cref="MatchingRule"/>.</summary>
[JsonConverter(typeof(MatchingRuleIdJsonConverter))]
public readonly record struct MatchingRuleId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator MatchingRuleId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(MatchingRuleId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static MatchingRuleId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class MatchingRuleIdJsonConverter : JsonConverter<MatchingRuleId>
{
    public override MatchingRuleId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("MatchingRuleId must be a non-null string.");
        return new MatchingRuleId(str);
    }

    public override void Write(Utf8JsonWriter writer, MatchingRuleId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
