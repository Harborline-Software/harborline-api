using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Identifier for a <see cref="ReusableUnit"/> — the D4 reuse-unit primitive
/// (ADR 0135 amendment 2026-07-01; locality semantics in the ADR 0140 amendment of
/// the same date).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="FormDefinitionId"/>: a reusable unit is a
/// <em>reference-able fragment</em> (a form-item subtree — or, by the same model, a
/// workflow subgraph) that a definition points at by <c>(Id, Version)</c>, not a
/// standalone form. The id is reusable across immutable versions; the
/// <c>(Tenant, Id, Version)</c> tuple is unique in the unit store.
/// </para>
/// <para>
/// Wire form is an opaque non-empty UTF-8 string; recommended grammar
/// <c>{authority}/{name}</c> (for example <c>tenant:acme/address-block</c>). Ids are
/// compared by exact case-sensitive string match.
/// </para>
/// </remarks>
[JsonConverter(typeof(ReusableUnitIdJsonConverter))]
public readonly record struct ReusableUnitId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string for ergonomic literal usage.</summary>
    public static implicit operator ReusableUnitId(string value) => new(value);

    /// <summary>Implicit conversion to string for log / DB serialization.</summary>
    public static implicit operator string(ReusableUnitId id) => id.Value;
}

internal sealed class ReusableUnitIdJsonConverter : JsonConverter<ReusableUnitId>
{
    public override ReusableUnitId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("ReusableUnitId must be a non-null string.");
        return new ReusableUnitId(str);
    }

    public override void Write(Utf8JsonWriter writer, ReusableUnitId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
