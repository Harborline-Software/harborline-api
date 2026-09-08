using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>Opaque identifier for a <see cref="Reconciliation"/>.</summary>
[JsonConverter(typeof(ReconciliationIdJsonConverter))]
public readonly record struct ReconciliationId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator ReconciliationId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(ReconciliationId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static ReconciliationId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class ReconciliationIdJsonConverter : JsonConverter<ReconciliationId>
{
    public override ReconciliationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("ReconciliationId must be a non-null string.");
        return new ReconciliationId(str);
    }

    public override void Write(Utf8JsonWriter writer, ReconciliationId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
