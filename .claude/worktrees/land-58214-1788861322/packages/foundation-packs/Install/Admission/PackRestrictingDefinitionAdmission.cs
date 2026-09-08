using System.Text.Json;

using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.Packs.Install.Admission;

/// <summary>
/// Reads the closed restricting-kind discriminators from composed pack content and routes each one
/// through the same validator used by ordinary definition writers. Unknown presentation/view kinds
/// are deliberately not inspected: permitting definitions retain their inert behavior.
/// </summary>
public sealed class PackRestrictingDefinitionAdmission
{
    private readonly IRestrictingDefinitionKindValidator _kinds;

    /// <summary>Constructs the pack-content adapter over the canonical kind validator.</summary>
    public PackRestrictingDefinitionAdmission(IRestrictingDefinitionKindValidator kinds)
        => _kinds = kinds ?? throw new ArgumentNullException(nameof(kinds));

    /// <summary>Returns every unknown restricting kind found in the composed definitions.</summary>
    public IReadOnlyList<PackAdmissionRefusal> Validate(IReadOnlyList<PackComposedItem> composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        var refusals = new List<PackAdmissionRefusal>();

        foreach (var item in composed)
        {
            try
            {
                using var document = JsonDocument.Parse(item.CanonicalJson);
                Visit(document.RootElement, item, parentProperty: null, refusals);
            }
            catch (JsonException)
            {
                // The concrete content parser owns malformed JSON and its family-specific code.
            }
        }

        return refusals;
    }

    private void Visit(
        JsonElement element,
        PackComposedItem item,
        string? parentProperty,
        ICollection<PackAdmissionRefusal> refusals)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                Visit(child, item, parentProperty, refusals);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // RuleDefinition: action + expression are its stable structural signature. Form actions
        // carry a kind but no expression, so permitting/action UI content cannot be caught here.
        if (TryGet(element, "action", out var action) && TryGet(element, "expression", out _))
        {
            Add(
                RestrictingDefinitionKindFamily.RuleAction,
                Text(action),
                TryGetString(element, "id"),
                item,
                refusals);
        }

        // PolicyEffect: kind + triggers are its stable structural signature.
        if (TryGet(element, "kind", out var policyKind) && TryGet(element, "triggers", out _))
        {
            Add(
                RestrictingDefinitionKindFamily.Policy,
                Text(policyKind),
                TryGetString(element, "id"),
                item,
                refusals);
        }

        // AuditRetentionPolicy's closed discriminator. Also accept a future retention definition's
        // conventional { retention: { kind } } shape without treating unrelated permitting kinds as
        // restricting.
        if (TryGet(element, "jurisdictionPreset", out var preset))
        {
            Add(RestrictingDefinitionKindFamily.Retention, Text(preset), null, item, refusals);
        }
        else if (string.Equals(parentProperty, "retention", StringComparison.OrdinalIgnoreCase)
            && TryGet(element, "kind", out var retentionKind))
        {
            Add(RestrictingDefinitionKindFamily.Retention, Text(retentionKind), null, item, refusals);
        }

        foreach (var property in element.EnumerateObject())
        {
            Visit(property.Value, item, property.Name, refusals);
        }
    }

    private void Add(
        RestrictingDefinitionKindFamily family,
        string kind,
        string? nestedId,
        PackComposedItem item,
        ICollection<PackAdmissionRefusal> refusals)
    {
        var refusal = _kinds.Validate(family, item.Key, kind, item.PackageKey, nestedId);
        if (refusal is not null)
        {
            refusals.Add(new PackAdmissionRefusal(item.Key, refusal.Code, refusal.Message));
        }
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : value.GetRawText();

    private static string? TryGetString(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
