using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Identifier for a <see cref="FormDefinition"/> definition (ADR 0055 keystone).
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from <c>Harborline.Api.Foundation.Assets.Common.SchemaId</c></b>
/// (which identifies a raw JSON Schema document registered in the kernel
/// schema registry). A <see cref="FormDefinitionId"/> identifies the foundation-tier
/// composite — a JSON Schema document plus the Harborline overlay (sections,
/// rules, permissions, i18n) plus lifecycle metadata. A <see cref="FormDefinition"/>
/// holds a <c>JsonSchema</c> document by value; the kernel-tier schema id
/// referenced by content-address is an implementation detail of how that
/// document is canonicalised, not part of the keystone keystone surface.
/// </para>
/// <para>
/// Wire form is an opaque non-empty UTF-8 string. Recommended grammar
/// <c>{authority}/{name}</c> (for example <c>tenant:acme/property-listing</c>);
/// callers are not required to honour the recommendation but the registry
/// will compare ids by exact case-sensitive string match.
/// </para>
/// </remarks>
[JsonConverter(typeof(FormDefinitionIdJsonConverter))]
public readonly record struct FormDefinitionId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion from string for ergonomic literal usage.</summary>
    public static implicit operator FormDefinitionId(string value) => new(value);

    /// <summary>Implicit conversion to string for log / DB serialization.</summary>
    public static implicit operator string(FormDefinitionId id) => id.Value;
}

internal sealed class FormDefinitionIdJsonConverter : JsonConverter<FormDefinitionId>
{
    public override FormDefinitionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString() ?? throw new JsonException("FormDefinitionId must be a non-null string.");
        return new FormDefinitionId(str);
    }

    public override void Write(Utf8JsonWriter writer, FormDefinitionId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
