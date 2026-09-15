using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>An explicit, versioned declaration of the catalogue fields supplying a read-only form.</summary>
[JsonConverter(typeof(CatalogueFieldSourceJsonConverter))]
public sealed record CatalogueFieldSource(
    string CapabilityId,
    int CoordinateSchemaVersion,
    int SourceMappingSchemaVersion,
    string SourceKind,
    IReadOnlyList<CatalogueFieldMapping> Fields);

public sealed record CatalogueFieldMapping(string FieldId, string Source);

/// <summary>Coordinates identify one field of an exact immutable source revision.</summary>
[JsonConverter(typeof(CatalogueFieldCoordinateJsonConverter))]
public sealed record CatalogueFieldCoordinate(int SchemaVersion, string Kind, string Id, string Version, string Field);

public sealed record CatalogueFieldProvenance(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackVersion = null);

[JsonConverter(typeof(CatalogueFieldSourceBindingJsonConverter))]
public sealed record CatalogueFieldSourceBinding(string DefinitionHash, CatalogueFieldProvenance Provenance);

[JsonConverter(typeof(CatalogueFieldReadRequestJsonConverter))]
public sealed record CatalogueFieldReadRequest(CatalogueFieldCoordinate Coordinate, CatalogueFieldSourceBinding SourceBinding);

/// <summary>Stable logical contract refusals; transport and authorization retain their existing envelopes.</summary>
public static class CatalogueFieldSourceCodes
{
    public const string LegacyMappingAbsent = "catalogue-field-source.legacy-mapping-absent";
    public const string MissingSupportDeclaration = "catalogue-field-source.missing-support-declaration";
    public const string MalformedSourceMapping = "catalogue-field-source.malformed-source-mapping";
    public const string UnknownSourceMapping = "catalogue-field-source.unknown-source-mapping";
    public const string UnsupportedCapabilityOrSchemaVersion = "catalogue-field-source.unsupported-capability-or-schema-version";
    public const string UnsupportedSourceMapping = "catalogue-field-source.unsupported-source-mapping";
    public const string MalformedCoordinate = "catalogue-field-source.malformed-coordinate";
    public const string UnknownCoordinate = "catalogue-field-source.unknown-coordinate";
    public const string UnsupportedCoordinate = "catalogue-field-source.unsupported-coordinate";
    public const string MalformedSourceBinding = "catalogue-field-source.malformed-source-binding";
    public const string SourceVersionUnavailable = "catalogue-field-source.source-version-unavailable";
    public const string SourceBindingMismatch = "catalogue-field-source.source-binding-mismatch";
    public const string SourceChangedAfterAuthorization = "catalogue-field-source.source-changed-after-authorization";
    public const string PayloadBindingMismatch = "catalogue-field-source.payload-binding-mismatch";
    public const string ReadOnly = "catalogue-field-source.read-only";
}

public sealed class CatalogueFieldSourceException(string code) : JsonException(code)
{
    public string Code { get; } = code;
}

internal sealed class CatalogueFieldSourceJsonConverter : JsonConverter<CatalogueFieldSource>
{
    public override bool HandleNull => true;

    public override CatalogueFieldSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return CatalogueFieldSourceContract.ParseDeclaration(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, CatalogueFieldSource value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("capabilityId", value.CapabilityId);
        writer.WriteNumber("coordinateSchemaVersion", value.CoordinateSchemaVersion);
        writer.WriteNumber("sourceMappingSchemaVersion", value.SourceMappingSchemaVersion);
        writer.WriteString("sourceKind", value.SourceKind);
        writer.WriteStartArray("fields");
        foreach (var field in value.Fields)
        {
            writer.WriteStartObject();
            writer.WriteString("fieldId", field.FieldId);
            writer.WriteString("source", field.Source);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

internal sealed class CatalogueFieldReadRequestJsonConverter : JsonConverter<CatalogueFieldReadRequest>
{
    public override bool HandleNull => true;

    public override CatalogueFieldReadRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return CatalogueFieldSourceContract.ParseRequest(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, CatalogueFieldReadRequest value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WritePropertyName("coordinate");
        JsonSerializer.Serialize(writer, value.Coordinate, CatalogueFieldSourceContract.WireOptions);
        writer.WritePropertyName("sourceBinding");
        JsonSerializer.Serialize(writer, value.SourceBinding, CatalogueFieldSourceContract.WireOptions);
        writer.WriteEndObject();
    }
}

internal sealed class CatalogueFieldCoordinateJsonConverter : JsonConverter<CatalogueFieldCoordinate>
{
    public override bool HandleNull => true;
    public override CatalogueFieldCoordinate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return CatalogueFieldSourceContract.ParseCoordinate(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, CatalogueFieldCoordinate value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", value.SchemaVersion);
        writer.WriteString("kind", value.Kind);
        writer.WriteString("id", value.Id);
        writer.WriteString("version", value.Version);
        writer.WriteString("field", value.Field);
        writer.WriteEndObject();
    }
}

internal sealed class CatalogueFieldSourceBindingJsonConverter : JsonConverter<CatalogueFieldSourceBinding>
{
    public override bool HandleNull => true;
    public override CatalogueFieldSourceBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return CatalogueFieldSourceContract.ParseBinding(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, CatalogueFieldSourceBinding value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteString("definitionHash", value.DefinitionHash);
        writer.WritePropertyName("provenance");
        JsonSerializer.Serialize(writer, value.Provenance, CatalogueFieldSourceContract.WireOptions);
        writer.WriteEndObject();
    }
}
