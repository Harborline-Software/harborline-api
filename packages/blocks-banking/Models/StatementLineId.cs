using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>Opaque identifier for a <see cref="StatementLine"/>.</summary>
[JsonConverter(typeof(StatementLineIdJsonConverter))]
public readonly record struct StatementLineId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator StatementLineId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(StatementLineId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static StatementLineId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class StatementLineIdJsonConverter : JsonConverter<StatementLineId>
{
    public override StatementLineId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("StatementLineId must be a non-null string.");
        return new StatementLineId(str);
    }

    public override void Write(Utf8JsonWriter writer, StatementLineId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
