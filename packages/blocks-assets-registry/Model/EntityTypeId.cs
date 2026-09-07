using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// Opaque identifier for an <see cref="EntityType"/> registry row (annex §3.2 / D-G).
/// </summary>
/// <remarks>Mirrors the opaque-string-backed id convention of <c>EquipmentId</c>.</remarks>
[JsonConverter(typeof(EntityTypeIdJsonConverter))]
public readonly record struct EntityTypeId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator EntityTypeId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(EntityTypeId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static EntityTypeId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class EntityTypeIdJsonConverter : JsonConverter<EntityTypeId>
{
    public override EntityTypeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("EntityTypeId must be a non-null string.");
        return new EntityTypeId(str);
    }

    public override void Write(Utf8JsonWriter writer, EntityTypeId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
