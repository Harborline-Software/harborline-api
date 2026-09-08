using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Opaque identifier for a <see cref="LegalEntityOwnership"/> edge in the
/// entity ownership graph (ADR 0104 §2.1).
/// </summary>
[JsonConverter(typeof(LegalEntityOwnershipIdJsonConverter))]
public readonly record struct LegalEntityOwnershipId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator LegalEntityOwnershipId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(LegalEntityOwnershipId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static LegalEntityOwnershipId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class LegalEntityOwnershipIdJsonConverter : JsonConverter<LegalEntityOwnershipId>
{
    public override LegalEntityOwnershipId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("LegalEntityOwnershipId must be a non-null string.");
        return new LegalEntityOwnershipId(str);
    }

    public override void Write(Utf8JsonWriter writer, LegalEntityOwnershipId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
