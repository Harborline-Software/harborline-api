using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// The discipline dimension of a type binding or a scored item — electrical / plumbing / HVAC /
/// … <b>or GENERIC</b> (annex §3.3, §3.8). A small pack-seeded, tenant-extensible registry
/// modelled here as an opaque string tag rather than a closed enum, so packs and tenants add
/// disciplines as configuration.
/// </summary>
/// <remarks>
/// The sentinel <see cref="Generic"/> (empty value) marks items that ride the general / leasing
/// inspection rather than a specialist discipline — the "GENERIC" band of the D-P roll-up
/// (item → category → discipline-or-generic → composite).
/// </remarks>
[JsonConverter(typeof(DisciplineTagJsonConverter))]
public readonly record struct DisciplineTag(string Value)
{
    /// <summary>The generic (non-discipline-specific) tag — general/leasing inspection items.</summary>
    public static readonly DisciplineTag Generic = new(string.Empty);

    /// <summary>True when this tag is the <see cref="Generic"/> sentinel.</summary>
    public bool IsGeneric => string.IsNullOrEmpty(Value);

    /// <inheritdoc />
    public override string ToString() => IsGeneric ? "(generic)" : Value;

    /// <summary>Implicit conversion from string.</summary>
    public static implicit operator DisciplineTag(string value) => new(value ?? string.Empty);
}

internal sealed class DisciplineTagJsonConverter : JsonConverter<DisciplineTag>
{
    public override DisciplineTag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, DisciplineTag value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
