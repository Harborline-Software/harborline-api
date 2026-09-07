using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Parses the pinned pack content contract for a <c>FormDefinition</c>. New bodies carry the
/// authoring route's <see cref="SaveFormDefinitionRequest"/> shape plus its definition envelope;
/// legacy request-only bodies remain readable. Identity, version, and tenant must agree with the
/// verified pack item and install context before projection.
/// </summary>
internal static class PackFormDefinitionContent
{
    private const string MoneyPattern = @"^$|^-?[0-9]+(\.[0-9]+)?$";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool TryParse(
        JsonNode? content,
        out SaveFormDefinitionRequest request,
        out string error)
        => TryParse(content, out request, out _, out error);

    public static bool TryParse(
        JsonNode? content,
        out SaveFormDefinitionRequest request,
        out DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>? envelope,
        out string error)
    {
        request = null!;
        envelope = null;
        error = string.Empty;

        if (content is not JsonObject)
        {
            error = "content is not a JSON object";
            return false;
        }

        try
        {
            request = content.Deserialize<SaveFormDefinitionRequest>(JsonOptions)!;
            if (content["definitionEnvelope"] is { } envelopeNode)
            {
                envelope = envelopeNode.Deserialize<DefinitionEnvelope<
                    FormDefinitionId,
                    SemanticVersion,
                    TenantId,
                    FormDefinitionProvenance>>(JsonOptions);
                if (envelope is null || envelope.Requires is null)
                {
                    error = "'definitionEnvelope' requires all eight members";
                    return false;
                }
            }
        }
        catch (JsonException ex)
        {
            error = $"content does not match SaveFormDefinitionRequest: {ex.Message}";
            return false;
        }

        if (request?.Overlay is null)
        {
            error = "missing 'overlay' object";
            return false;
        }

        if (request.Draft == true)
        {
            // Pack items always PUBLISH on projection — a 'draft: true' flag in signed pack
            // content would be silently meaningless. Refuse it fail-closed rather than let the
            // flag pretend to stage content.
            error = "'draft' is not valid in pack content: pack items always publish on projection";
            return false;
        }

        if (request.Overlay.Fields is null)
        {
            error = "overlay requires a 'fields' object";
            return false;
        }

        if (request.Overlay.Sections is null || request.Overlay.Sections.Count == 0)
        {
            error = "overlay requires at least one section";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Projects a published form back onto the pinned pack contract. The form store owns the overlay while
    /// the kernel registry owns its synthesized JSON Schema, so both are required to reconstruct the same
    /// <see cref="SaveFormDefinitionRequest"/> the install projector consumes. This avoids silently
    /// weakening required fields, options, or validation constraints during an authoring round-trip.
    /// </summary>
    public static JsonNode ToContent(FormDefinition definition, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(schema);

        if (JsonNode.Parse(schema.JsonSchemaText) is not JsonObject schemaRoot)
        {
            throw new JsonException("form schema is not a JSON object");
        }

        var overlay = OverlayDto.From(definition.Overlay);
        var fieldsMeta = overlay.Fields.ToDictionary(
            entry => entry.Key,
            entry => ToFieldMeta(entry.Key, entry.Value, schemaRoot),
            StringComparer.Ordinal);
        var request = new SaveFormDefinitionRequest(overlay, fieldsMeta);
        var content = JsonSerializer.SerializeToNode(request, JsonOptions) as JsonObject
            ?? throw new JsonException("form definition did not serialize to a JSON object");
        // Crossing the signed-pack boundary changes the authority of the transported definition.
        // The exporter therefore stamps Pack; the projector still rejects a hand-authored envelope
        // that claims Base/Tenant/Instance instead of trusting that claim.
        content["definitionEnvelope"] = JsonSerializer.SerializeToNode(
            definition.Envelope with { CascadeLayer = CascadeLayer.Pack }, JsonOptions)
            ?? throw new JsonException("form definition envelope did not serialize to JSON");
        return content;
    }

    private static FieldMetaDto ToFieldMeta(string key, FieldOverlayDto overlay, JsonObject schemaRoot)
    {
        var located = FindProperty(schemaRoot, key)
            ?? throw new JsonException($"form schema has no property for overlay field '{key}'");
        var fieldSchema = located.Schema;
        var type = string.IsNullOrWhiteSpace(overlay.ControlHint)
            ? InferControlType(fieldSchema)
            : overlay.ControlHint;

        var options = fieldSchema["enum"] is JsonArray values
            ? values.Select(v => v?.GetValue<string>() ?? string.Empty).ToList()
            : null;
        var validations = new List<FieldValidationDto>();
        AddNumericKeyword(fieldSchema, validations, "minLength",
            skip: located.Required && fieldSchema["minLength"]?.GetValue<int>() == 1);
        AddNumericKeyword(fieldSchema, validations, "maxLength");
        AddNumericKeyword(fieldSchema, validations, "minimum");
        AddNumericKeyword(fieldSchema, validations, "maximum");
        if (fieldSchema["pattern"] is JsonValue patternValue
            && patternValue.TryGetValue<string>(out var pattern)
            && !(string.Equals(type, "currency", StringComparison.Ordinal) && pattern == MoneyPattern))
        {
            validations.Add(new FieldValidationDto("pattern", pattern));
        }

        return new FieldMetaDto(
            Type: type ?? "text",
            Required: located.Required,
            Options: options,
            Validations: validations.Count == 0 ? null : validations);
    }

    private static void AddNumericKeyword(
        JsonObject schema,
        ICollection<FieldValidationDto> validations,
        string keyword,
        bool skip = false)
    {
        if (!skip && schema[keyword] is JsonValue value)
        {
            validations.Add(new FieldValidationDto(keyword, value.ToJsonString()));
        }
    }

    private static string InferControlType(JsonObject schema)
    {
        if (schema["enum"] is JsonArray) return "select";
        if (schema["format"]?.GetValue<string>() == "date") return "date";
        return schema["type"]?.GetValue<string>() switch
        {
            "number" or "integer" => "number",
            "boolean" => "checkbox",
            _ => "text",
        };
    }

    private static LocatedProperty? FindProperty(JsonObject objectSchema, string key)
    {
        if (objectSchema["properties"] is not JsonObject properties)
        {
            return null;
        }

        var required = objectSchema["required"] is JsonArray requiredValues
            && requiredValues.Any(v => string.Equals(v?.GetValue<string>(), key, StringComparison.Ordinal));
        if (properties[key] is JsonObject direct)
        {
            return new LocatedProperty(direct, required);
        }

        foreach (var child in properties)
        {
            if (child.Value is not JsonObject childSchema) continue;
            var nestedObject = childSchema["items"] as JsonObject ?? childSchema;
            var located = FindProperty(nestedObject, key);
            if (located is not null) return located;
        }
        return null;
    }

    private readonly record struct LocatedProperty(JsonObject Schema, bool Required);
}
