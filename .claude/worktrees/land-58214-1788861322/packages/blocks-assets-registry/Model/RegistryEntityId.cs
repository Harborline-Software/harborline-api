using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// Opaque identifier for a <see cref="RegistryEntity"/> — the <b>generic typed-entity
/// reference</b> that the whole Asset Type System keys off.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR 0101 Rev 3.1 / council finding <b>A4</b>, condition assessments and scoring
/// records reference this generic entity id, <b>never</b> a concrete
/// <c>Harborline.Api.Blocks.Assets.Domain.AssetId</c> or
/// <c>Harborline.Api.Blocks.PropertyEquipment.Models.EquipmentId</c>. That is the single
/// Wave-1 shape decision that keeps the later promotion wave a bridge rather than a
/// rewrite: both new registry entities and (later) promoted Equipment/Asset resolve into
/// one condition history through this ref.
/// </para>
/// <para>Mirrors the opaque-string-backed id convention of <c>EquipmentId</c>.</para>
/// </remarks>
[JsonConverter(typeof(RegistryEntityIdJsonConverter))]
public readonly record struct RegistryEntityId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator RegistryEntityId(string value) => new(value);

    /// <summary>Implicit conversion to string.</summary>
    public static implicit operator string(RegistryEntityId id) => id.Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static RegistryEntityId NewId() => new(Guid.NewGuid().ToString());
}

internal sealed class RegistryEntityIdJsonConverter : JsonConverter<RegistryEntityId>
{
    public override RegistryEntityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("RegistryEntityId must be a non-null string.");
        return new RegistryEntityId(str);
    }

    public override void Write(Utf8JsonWriter writer, RegistryEntityId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
