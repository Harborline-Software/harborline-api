namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// Canonical string values for a registry row's optional PII-sensitivity default.
/// Mirrors <c>Harborline.Api.Foundation.Forms.PiiSensitivity</c> (the adapter deliberately does
/// NOT reference the forms substrate — like ui-react, the renderer stays registry-agnostic
/// and carries the classification as an opaque token the emitter reads).
/// </summary>
public static class PiiSensitivityValues
{
    /// <summary>Not sensitive; stored in cleartext.</summary>
    public const string None = "None";

    /// <summary>Sensitive (PII / financial / health); tenant-key-encrypted at rest.
    /// ADR 0168 D4's three reserved spatial field types default here.</summary>
    public const string Sensitive = "Sensitive";
}

/// <summary>
/// The stable per-field-type contract — the Blazor parity mirror of the React Harborline App's
/// <c>FieldTypeDefinition</c> (<c>src/forms/builder/fieldTypes.ts</c>,
/// ADR 0055 Rev 10). Built-in AND extension field types register as one of these records;
/// nothing in the renderer hardcodes a per-type switch.
/// </summary>
/// <param name="Type">The builder field-type id (e.g. <c>"currency"</c>).</param>
/// <param name="ControlHint">The renderer control the composed control registry keys off.</param>
/// <param name="ValueKind">Value shape stored in the candidate document.</param>
/// <param name="CapabilityId">
/// The OWNING capability (ADR 0055 Rev 10 D-R10.1 — REQUIRED, never optional: an absent
/// owner defaulting to core would be the fail-open shape). The ADR 0154 admission-vocabulary
/// <c>CapabilityId</c> (<c>&lt;area&gt;.&lt;name&gt;</c>), NOT the ADR 0123
/// provider-resolution id. <see cref="FieldTypeRegistry.Compose"/> rejects an empty value.
/// </param>
/// <param name="LabelKey">Localization key suffix for the palette label (<c>forms.builder.fieldType.&lt;key&gt;</c>).</param>
/// <param name="PiiSensitivityDefault">
/// Optional creation-time PII default the definition emitter reads (ADR 0055 Rev 10
/// D-R10.6 / ADR 0168 D4). Resolved via
/// <see cref="FieldTypeRegistry.ResolvePiiSensitivityDefault"/> — absent means
/// <see cref="PiiSensitivityValues.None"/>. A creation-time default, never a runtime
/// reclassifier: editing a registry row must not change already-authored fields.
/// </param>
public sealed record FieldTypeDefinition(
    string Type,
    string ControlHint,
    FieldValueKind ValueKind,
    string CapabilityId,
    string? LabelKey = null,
    string? PiiSensitivityDefault = null);
