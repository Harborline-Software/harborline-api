namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// The value shape a field of a given type stores in the candidate document — the Blazor
/// mirror of the React Harborline App registry's <c>FieldValueKind</c> union
/// (<c>src/forms/builder/fieldTypes.ts</c>, ADR 0055 Rev 10). Records INTENT
/// (the server owns JSON-Schema derivation); notably <see cref="Decimal"/> is a decimal
/// STRING on the wire, never an IEEE-754 double, so cent precision survives.
/// </summary>
public enum FieldValueKind
{
    /// <summary>A plain string value.</summary>
    String = 0,

    /// <summary>A number value.</summary>
    Number,

    /// <summary>A decimal STRING (currency) — never a floating-point number.</summary>
    Decimal,

    /// <summary>A boolean value.</summary>
    Boolean,

    /// <summary>An array of strings (multiselect).</summary>
    StringArray,

    /// <summary>A list of file references.</summary>
    FileList,

    /// <summary>A structured object leaf (address composite / captured signature / spatial value per ADR 0168).</summary>
    Object,
}

/// <summary>
/// The renderer-side reading of a field's declared value kind. Per ADR 0055 Rev 10
/// D-R10.5 the renderer stays registry-agnostic: the ONLY semantics it attaches to a
/// field's <c>valueKind</c> string is <c>== "string"</c> — everything else is
/// "structured, and I have no control for it", which MUST fail closed.
/// </summary>
public static class SchemaValueKinds
{
    /// <summary>The canonical wire token for the string value kind.</summary>
    public const string StringKind = "string";

    /// <summary>
    /// True when <paramref name="valueKind"/> declares a structured (non-string) value.
    /// An absent kind preserves the legacy <c>FormView</c> shape, whose unknown hints
    /// rendered as text fields — so <see langword="null"/> reads as string.
    /// </summary>
    public static bool IsStructured(string? valueKind)
        => valueKind is not null
           && !string.Equals(valueKind, StringKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>The wire token for a registry-declared <see cref="FieldValueKind"/>.</summary>
    public static string ToWireToken(FieldValueKind kind) => kind switch
    {
        FieldValueKind.String => "string",
        FieldValueKind.Number => "number",
        FieldValueKind.Decimal => "decimal",
        FieldValueKind.Boolean => "boolean",
        FieldValueKind.StringArray => "stringArray",
        FieldValueKind.FileList => "fileList",
        FieldValueKind.Object => "object",
        _ => "object", // unknown future kinds read as structured — the fail-closed direction
    };
}
