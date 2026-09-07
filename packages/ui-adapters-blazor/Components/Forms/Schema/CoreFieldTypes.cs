using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// The CORE field-type rows + core control renderers for the Blazor schema surface — the
/// parity mirror of the React Harborline App registry
/// (<c>src/forms/builder/fieldTypes.ts</c>, ADR 0055 Rev 10). Every row
/// declares the core owning capability (D-R10.1); the shipped registry is
/// <see cref="Registry"/> = <c>Compose(core, coreControls, FieldTypeExtensionModule.Extensions)</c>.
/// </summary>
public static class CoreFieldTypes
{
    /// <summary>
    /// The owning capability for every built-in row — ADR 0154 admission vocabulary,
    /// <c>&lt;area&gt;.&lt;name&gt;</c> shape (CIC-confirmed 2026-08-05, ADR 0055 Rev 10 D-R10.1).
    /// </summary>
    public const string CoreFieldTypeCapability = "forms.dynamic-forms";

    private static FieldTypeDefinition Row(
        string type, string hint, FieldValueKind kind, string? pii = null)
        => new(type, hint, kind, CoreFieldTypeCapability, LabelKey: type, PiiSensitivityDefault: pii);

    /// <summary>
    /// The built-in rows, in palette order — mirrors the React registry's 22 rows
    /// (7 original flat types, then the F-17 Wave 1/2 additions, then condition-rating).
    /// </summary>
    public static IReadOnlyList<FieldTypeDefinition> Definitions { get; } = new[]
    {
        Row("text", "text", FieldValueKind.String),
        Row("textarea", "textarea", FieldValueKind.String),
        Row("number", "number", FieldValueKind.Number),
        Row("date", "date", FieldValueKind.String),
        Row("select", "select", FieldValueKind.String),
        // radio + select both project to the option-list control; radio is a presentation
        // variant the configurator records for round-trip (mirrors fieldTypes.ts).
        Row("radio", "select", FieldValueKind.String),
        Row("checkbox", "checkbox", FieldValueKind.Boolean),
        Row("currency", "currency", FieldValueKind.Decimal),
        Row("email", "email", FieldValueKind.String),
        Row("datetime", "datetime", FieldValueKind.String),
        Row("multiselect", "multiselect", FieldValueKind.StringArray),
        Row("file", "file", FieldValueKind.FileList),
        Row("percentage", "percentage", FieldValueKind.Number),
        Row("time", "time", FieldValueKind.String),
        Row("boolean-toggle", "boolean-toggle", FieldValueKind.Boolean),
        Row("phone", "phone", FieldValueKind.String),
        Row("url", "url", FieldValueKind.String),
        Row("signature", "signature", FieldValueKind.Object),
        Row("address-composite", "address", FieldValueKind.Object),
        Row("readonly", "readonly", FieldValueKind.String),
        Row("hidden", "hidden", FieldValueKind.String),
        Row("condition-rating", "condition-rating", FieldValueKind.Number),
    };

    /// <summary>
    /// The core control renderers, keyed by control hint. Deliberately the honest subset:
    /// hints whose Blazor control has not been built yet (signature, address, file,
    /// multiselect, condition-rating — all structured value kinds) are NOT registered,
    /// so <see cref="SchemaControlResolver"/> fails closed to the read-only affordance
    /// for them rather than presenting an editable text input (D-R10.5).
    /// </summary>
    public static IReadOnlyDictionary<string, SchemaControlRenderer> Controls { get; } =
        new Dictionary<string, SchemaControlRenderer>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = Input("text"),
            ["textarea"] = TextArea(),
            ["email"] = Input("email"),
            ["phone"] = Input("tel"),
            ["url"] = Input("url"),
            ["number"] = Input("number"),
            ["currency"] = Input("text"), // decimal STRING — never a float input
            ["percentage"] = Input("number"),
            ["date"] = Input("date"),
            ["datetime"] = Input("datetime-local"),
            ["time"] = Input("time"),
            ["select"] = Input("text"), // option list arrives with the FormView renderer card
            ["checkbox"] = Checkbox(),
            ["boolean-toggle"] = Checkbox(),
            ["readonly"] = ReadonlyOutput(),
            ["hidden"] = Hidden(),
        };

    /// <summary>The shipped composed registry: core + the one substitutable extension module.</summary>
    public static ComposedFieldTypeRegistry Registry { get; } = FieldTypeRegistry.Compose(
        Definitions, Controls, FieldTypeExtensionModule.Extensions);

    private static SchemaControlRenderer Input(string type) => ctx => builder =>
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "type", type);
        builder.AddAttribute(2, "id", ctx.FieldName);
        builder.AddAttribute(3, "name", ctx.FieldName);
        builder.AddAttribute(4, "value", ctx.StringValue);
        AddCommonAttributes(builder, ctx);
        builder.CloseElement();
    };

    private static SchemaControlRenderer TextArea() => ctx => builder =>
    {
        builder.OpenElement(0, "textarea");
        builder.AddAttribute(1, "id", ctx.FieldName);
        builder.AddAttribute(2, "name", ctx.FieldName);
        AddCommonAttributes(builder, ctx);
        builder.AddContent(8, ctx.StringValue);
        builder.CloseElement();
    };

    private static SchemaControlRenderer Checkbox() => ctx => builder =>
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "type", "checkbox");
        builder.AddAttribute(2, "id", ctx.FieldName);
        builder.AddAttribute(3, "name", ctx.FieldName);
        if (string.Equals(ctx.StringValue, "true", StringComparison.OrdinalIgnoreCase))
        {
            builder.AddAttribute(4, "checked", true);
        }

        AddCommonAttributes(builder, ctx);
        builder.CloseElement();
    };

    private static SchemaControlRenderer ReadonlyOutput() => ctx => builder =>
    {
        // Display-only projection of a derived value — an <output>, never an input.
        builder.OpenElement(0, "output");
        builder.AddAttribute(1, "id", ctx.FieldName);
        builder.AddContent(2, string.IsNullOrEmpty(ctx.StringValue) ? "—" : ctx.StringValue);
        builder.CloseElement();
    };

    private static SchemaControlRenderer Hidden() => ctx => builder =>
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "type", "hidden");
        builder.AddAttribute(2, "name", ctx.FieldName);
        builder.AddAttribute(3, "value", ctx.StringValue);
        builder.CloseElement();
    };

    private static void AddCommonAttributes(RenderTreeBuilder builder, SchemaControlContext ctx)
    {
        if (ctx.Disabled)
        {
            builder.AddAttribute(5, "disabled", true);
        }

        if (ctx.HasError)
        {
            // ARIA state attribute from a bool renders via the lower-invariant string form.
            builder.AddAttribute(6, "aria-invalid", true.ToString().ToLowerInvariant());
        }
    }
}
