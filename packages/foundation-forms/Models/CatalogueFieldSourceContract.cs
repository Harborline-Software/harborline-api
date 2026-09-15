using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>Closed v1 vocabulary and strict wire parsing. Parsing does not establish runtime support.</summary>
public static class CatalogueFieldSourceContract
{
    public const string CapabilityId = "forms.catalogue-field-source";
    public const int CoordinateSchemaVersion = 1;
    public const int SourceMappingSchemaVersion = 1;
    public const string SourceKind = "FormDefinition";

    internal static JsonSerializerOptions WireOptions { get; } = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<CatalogueFieldMapping> Fields { get; } = ImmutableArray.Create(
        new CatalogueFieldMapping("formId", "catalogue.entry.formId"),
        new CatalogueFieldMapping("title", "catalogue.entry.title"),
        new CatalogueFieldMapping("version", "catalogue.entry.version"),
        new CatalogueFieldMapping("cascadeLayer", "catalogue.entry.cascadeLayer"));

    public static CatalogueFieldSource ParseDeclaration(JsonElement value)
    {
        const string malformed = CatalogueFieldSourceCodes.MalformedSourceMapping;
        if (value.ValueKind != JsonValueKind.Object) throw new CatalogueFieldSourceException(malformed);
        foreach (var required in new[] { "capabilityId", "coordinateSchemaVersion", "sourceMappingSchemaVersion" })
            if (!value.TryGetProperty(required, out _))
                throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.MissingSupportDeclaration);
        RequireObject(value, malformed, "capabilityId", "coordinateSchemaVersion", "sourceMappingSchemaVersion", "sourceKind", "fields");
        var capability = RequireString(value.GetProperty("capabilityId"), malformed);
        var coordinates = RequireInteger(value.GetProperty("coordinateSchemaVersion"), malformed, CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion);
        var mappings = RequireInteger(value.GetProperty("sourceMappingSchemaVersion"), malformed, CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion);
        var kind = RequireString(value.GetProperty("sourceKind"), malformed);
        var fields = value.GetProperty("fields");
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() != Fields.Count)
            throw new CatalogueFieldSourceException(malformed);
        var parsed = new List<CatalogueFieldMapping>();
        foreach (var field in fields.EnumerateArray())
        {
            RequireObject(field, malformed, "fieldId", "source");
            parsed.Add(new CatalogueFieldMapping(
                RequireString(field.GetProperty("fieldId"), malformed),
                RequireString(field.GetProperty("source"), malformed)));
        }
        if (!KnownKind(kind) || parsed.Any(p => !Fields.Any(f => f.FieldId == p.FieldId) || !Fields.Any(f => f.Source == p.Source)))
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.UnknownSourceMapping);
        if (!parsed.SequenceEqual(Fields)) throw new CatalogueFieldSourceException(malformed);
        if (capability != CapabilityId || coordinates != CoordinateSchemaVersion || mappings != SourceMappingSchemaVersion)
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion);
        if (kind != SourceKind) throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.UnsupportedSourceMapping);
        return new CatalogueFieldSource(capability, coordinates, mappings, kind, parsed.ToImmutableArray());
    }

    public static CatalogueFieldReadRequest ParseRequest(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("coordinate", out var coordinate)
            && !value.TryGetProperty("sourceBinding", out _))
        {
            _ = ParseCoordinate(coordinate);
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.MalformedSourceBinding);
        }
        RequireObject(value, CatalogueFieldSourceCodes.MalformedCoordinate, "coordinate", "sourceBinding");
        return new CatalogueFieldReadRequest(ParseCoordinate(value.GetProperty("coordinate")), ParseBinding(value.GetProperty("sourceBinding")));
    }

    public static CatalogueFieldCoordinate ParseCoordinate(JsonElement value)
    {
        const string malformed = CatalogueFieldSourceCodes.MalformedCoordinate;
        RequireObject(value, malformed, "schemaVersion", "kind", "id", "version", "field");
        var schema = RequireInteger(value.GetProperty("schemaVersion"), malformed, CatalogueFieldSourceCodes.UnsupportedCoordinate);
        var kind = RequireString(value.GetProperty("kind"), malformed);
        var id = RequireString(value.GetProperty("id"), malformed);
        var version = RequireString(value.GetProperty("version"), malformed);
        var field = RequireString(value.GetProperty("field"), malformed);
        if (!IsComponent(id) || !IsComponent(kind) || !IsComponent(field) || !IsCanonicalVersion(version))
            throw new CatalogueFieldSourceException(malformed);
        if (!KnownKind(kind) || !Fields.Any(f => f.FieldId == field))
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.UnknownCoordinate);
        if (schema != CoordinateSchemaVersion || kind != SourceKind)
            throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.UnsupportedCoordinate);
        return new CatalogueFieldCoordinate(schema, kind, id, version, field);
    }

    public static CatalogueFieldSourceBinding ParseBinding(JsonElement value)
    {
        const string malformed = CatalogueFieldSourceCodes.MalformedSourceBinding;
        RequireObject(value, malformed, "definitionHash", "provenance");
        var hash = RequireString(value.GetProperty("definitionHash"), malformed);
        if (hash.Length != 71 || !hash.StartsWith("sha256:", StringComparison.Ordinal)
            || hash.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new CatalogueFieldSourceException(malformed);
        var provenance = value.GetProperty("provenance");
        if (provenance.ValueKind != JsonValueKind.Object || !provenance.TryGetProperty("kind", out var kindValue))
            throw new CatalogueFieldSourceException(malformed);
        var kind = RequireString(kindValue, malformed);
        CatalogueFieldProvenance parsed;
        if (kind == "tenant")
        {
            RequireObject(provenance, malformed, "kind");
            parsed = new CatalogueFieldProvenance(kind);
        }
        else if (kind is "pack" or "platform")
        {
            RequireObject(provenance, malformed, "kind", "packKey", "packVersion");
            var key = RequireString(provenance.GetProperty("packKey"), malformed);
            var version = RequireString(provenance.GetProperty("packVersion"), malformed);
            if (key.Length == 0 || !PackValidator.IsPinned(version)) throw new CatalogueFieldSourceException(malformed);
            parsed = new CatalogueFieldProvenance(kind, key, version);
        }
        else throw new CatalogueFieldSourceException(malformed);
        return new CatalogueFieldSourceBinding(hash, parsed);
    }

    /// <summary>Validates exact Unicode components without normalization or percent decoding.</summary>
    public static bool IsComponent(string value)
    {
        if (value.Length == 0 || value is "." or ".." || value.Any(char.IsControl)) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    public static bool IsCanonicalVersion(string value) => value.Split('.') is { Length: 3 } parts
        && parts.All(p => p.Length > 0 && (p.Length == 1 || p[0] != '0')
            && p.All(c => c is >= '0' and <= '9')
            && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));

    /// <summary>Rejects decoded duplicate names at all depths of the opted-in content.</summary>
    public static void RequireUniqueMembers(JsonElement value, string code)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CatalogueFieldSourceException(code);
                RequireUniqueMembers(property.Value, code);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RequireUniqueMembers(child, code);
    }

    private static bool KnownKind(string kind) => Enum.TryParse<PackContentKind>(kind, out var parsed)
        && Enum.IsDefined(parsed) && parsed.ToString() == kind;

    private static void RequireObject(JsonElement value, string code, params string[] members)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CatalogueFieldSourceException(code);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !members.Contains(property.Name, StringComparer.Ordinal))
                throw new CatalogueFieldSourceException(code);
        if (names.Count != members.Length) throw new CatalogueFieldSourceException(code);
    }

    private static string RequireString(JsonElement value, string code)
    {
        if (value.ValueKind != JsonValueKind.String) throw new CatalogueFieldSourceException(code);
        try { return value.GetString()!; }
        catch (InvalidOperationException) { throw new CatalogueFieldSourceException(code); }
    }

    private static int RequireInteger(JsonElement value, string code, string unsupported)
    {
        if (value.ValueKind != JsonValueKind.Number
            || value.GetRawText().Any(c => c is not (>= '0' and <= '9') and not '-'))
            throw new CatalogueFieldSourceException(code);
        if (!value.TryGetInt32(out var number)) throw new CatalogueFieldSourceException(unsupported);
        return number;
    }
}
