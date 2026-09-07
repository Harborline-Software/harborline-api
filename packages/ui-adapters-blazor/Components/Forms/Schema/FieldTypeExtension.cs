namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// The palette group a field type is filed under — mirror of the React Harborline App's
/// <c>FieldTypeGroupId</c> union (ADR 0055 Rev 10).
/// </summary>
public enum FieldTypeGroup
{
    /// <summary>Text-shaped inputs (text / textarea / email / phone / url).</summary>
    Text = 0,

    /// <summary>Option-list inputs (select / radio / multiselect / checkbox / toggle).</summary>
    Choice,

    /// <summary>Date and time inputs.</summary>
    DateTime,

    /// <summary>Numeric inputs (number / currency / percentage).</summary>
    Numeric,

    /// <summary>Everything else (file / signature / address / readonly / hidden / spatial).</summary>
    Advanced,
}

/// <summary>
/// A build-time field-type extension: a registry ROW paired with its CONTROL, filed under
/// a palette group. The pairing is the mechanism that makes ADR 0168's "the registry row
/// lands with the control, never before" STRUCTURAL rather than procedural — a row without
/// a control is not expressible (ADR 0055 Rev 10 D-R10.2).
/// </summary>
/// <param name="Definition">The field-type registry row (carries the owning capability + optional PII default).</param>
/// <param name="Control">The schema control renderer for <c>Definition.ControlHint</c>.</param>
/// <param name="Group">The palette group the row is filed under.</param>
public sealed record FieldTypeExtension(
    FieldTypeDefinition Definition,
    SchemaControlRenderer Control,
    FieldTypeGroup Group);
