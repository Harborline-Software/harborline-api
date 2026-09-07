using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// Opaque identifier for a <see cref="TypedRelationship"/> dated edge (annex §3.1).
/// </summary>
/// <remarks>Mirrors the opaque-string-backed id convention of <c>EquipmentId</c>.</remarks>
[JsonConverter(typeof(TypedRelationshipIdJsonConverter))]
public readonly record struct TypedRelationshipId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator TypedRelationshipId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(TypedRelationshipId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static TypedRelationshipId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class TypedRelationshipIdJsonConverter : JsonConverter<TypedRelationshipId>
{
    public override TypedRelationshipId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("TypedRelationshipId must be a non-null string.");
        return new TypedRelationshipId(str);
    }

    public override void Write(Utf8JsonWriter writer, TypedRelationshipId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
