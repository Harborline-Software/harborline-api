using System.Text.Json;

using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Admission for the frozen catalogue-field-source profile. The default has no runtime support.</summary>
public sealed class CatalogueFieldSourceAdmission
{
    private readonly Func<CatalogueFieldSource, bool>? _runtimeSupports;

    /// <summary>
    /// A host may supply this check only from its registered, implemented field reader. Parsing a
    /// declaration or listing a capability does not implement that reader or establish support.
    /// </summary>
    public CatalogueFieldSourceAdmission(Func<CatalogueFieldSource, bool>? runtimeSupports = null)
        => _runtimeSupports = runtimeSupports;

    public string? ValidateSupport(CatalogueFieldSource? source) => source is null ? null
        : _runtimeSupports?.Invoke(source) == true ? null
        : CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion;

    public IReadOnlyList<PackAdmissionRefusal> Validate(IReadOnlyList<PackComposedItem> composed)
    {
        var refusals = new List<PackAdmissionRefusal>();
        foreach (var item in composed.Where(c => c.Kind == PackContentKind.FormDefinition))
        {
            try
            {
                using var document = JsonDocument.Parse(item.CanonicalJson);
                RequirePackDeclaration(document.RootElement, item.CapabilityRequirements);
                var source = ParseContent(document.RootElement);
                if (item.SeedCanonicalJson is { } seedJson)
                {
                    using var seed = JsonDocument.Parse(seedJson);
                    RequirePackDeclaration(seed.RootElement, item.CapabilityRequirements);
                    var seedSource = ParseContent(seed.RootElement);
                    if ((source is null) != (seedSource is null)
                        || (source is not null && seedSource is not null
                            && !source.Fields.SequenceEqual(seedSource.Fields)))
                        throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.MalformedSourceMapping);
                }
                if (source is null) continue;
                if (ValidateSupport(source) is { } code) throw new CatalogueFieldSourceException(code);
            }
            catch (CatalogueFieldSourceException ex)
            {
                refusals.Add(new PackAdmissionRefusal(item.Key, ex.Code, ex.Message));
            }
            catch (JsonException ex)
            {
                refusals.Add(new PackAdmissionRefusal(item.Key, CatalogueFieldSourceCodes.MalformedSourceMapping, ex.Message));
            }
        }
        return refusals;
    }

    private static void RequirePackDeclaration(JsonElement content, IReadOnlyList<string>? requirements)
    {
        if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("catalogueFieldSource", out _)) return;
        var count = requirements?.Count(c => c == CatalogueFieldSourceContract.CapabilityId) ?? 0;
        if (count == 0) throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.MissingSupportDeclaration);
        if (count != 1) throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.MalformedSourceMapping);
    }

    /// <summary>Strictly parses opt-in and its rendering correspondence without declaring runtime support.</summary>
    internal static CatalogueFieldSource? ParseContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
            return null;
        const string malformed = CatalogueFieldSourceCodes.MalformedSourceMapping;
        if (content.EnumerateObject().Any(p => p.Name != "catalogueFieldSource"
            && string.Equals(p.Name, "catalogueFieldSource", StringComparison.OrdinalIgnoreCase)))
            throw new CatalogueFieldSourceException(malformed);
        if (!content.TryGetProperty("catalogueFieldSource", out var declaration)) return null;
        CatalogueFieldSourceContract.RequireUniqueMembers(content, malformed);
        var source = CatalogueFieldSourceContract.ParseDeclaration(declaration);
        if (!content.TryGetProperty("overlay", out var overlay) || overlay.ValueKind != JsonValueKind.Object
            || !overlay.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("fieldsMeta", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !overlay.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            throw new CatalogueFieldSourceException(malformed);
        foreach (var field in source.Fields)
            if (!fields.TryGetProperty(field.FieldId, out var rendered) || rendered.ValueKind != JsonValueKind.Object
                || !meta.TryGetProperty(field.FieldId, out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                throw new CatalogueFieldSourceException(malformed);
        var renderedFields = new List<string>();
        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty("fields", out var names)
                || names.ValueKind != JsonValueKind.Array)
                throw new CatalogueFieldSourceException(malformed);
            foreach (var name in names.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.String) throw new CatalogueFieldSourceException(malformed);
                renderedFields.Add(name.GetString()!);
            }
            // Alternate recursive trees could replace the admitted flat field order or introduce actions.
            if (section.TryGetProperty("items", out var items) && items.ValueKind != JsonValueKind.Null
                && (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 0))
                throw new CatalogueFieldSourceException(malformed);
        }
        if (!renderedFields.SequenceEqual(source.Fields.Select(f => f.FieldId), StringComparer.Ordinal))
            throw new CatalogueFieldSourceException(malformed);
        if (overlay.TryGetProperty("rules", out var rules) && rules.ValueKind != JsonValueKind.Null)
        {
            if (rules.ValueKind != JsonValueKind.Array) throw new CatalogueFieldSourceException(malformed);
            foreach (var rule in rules.EnumerateArray())
                if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("action", out var action)
                    || action.ValueKind != JsonValueKind.String
                    || string.Equals(action.GetString(), "Compute", StringComparison.OrdinalIgnoreCase))
                    throw new CatalogueFieldSourceException(malformed);
        }
        if (overlay.TryGetProperty("wizard", out var wizard) && wizard.ValueKind != JsonValueKind.Null)
            throw new CatalogueFieldSourceException(malformed);
        return source;
    }
}
