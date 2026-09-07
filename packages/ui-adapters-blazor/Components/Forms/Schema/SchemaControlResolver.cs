using Microsoft.AspNetCore.Components;

namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// Fail-closed control resolution — the Blazor parity mirror of ui-react's
/// <c>renderControl</c> (<c>SchemaForm.tsx</c>, ADR 0055 Rev 10 D-R10.5, ratifying
/// Harborline card 3679; parity mandated for the .NET Harborline App by card 3698 / ADR 0167).
/// </summary>
public static class SchemaControlResolver
{
    /// <summary>
    /// Resolve the field's control from the composed registry:
    /// <list type="bullet">
    /// <item>a registered control for the hint renders normally;</item>
    /// <item>an UNRESOLVED hint whose declared value kind is structured (present and not
    /// <c>"string"</c>) renders <see cref="HarborlineUnresolvedControl"/> — a READ-ONLY,
    /// non-writing, localized affordance, NEVER an editable text input (a text box over a
    /// structured value would corrupt the candidate document on submit);</item>
    /// <item>an absent or <c>"string"</c> value kind keeps the text fallback, so every
    /// legacy <c>FormView</c> renders as before.</item>
    /// </list>
    /// </summary>
    public static RenderFragment Resolve(SchemaControlContext context, ComposedFieldTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);

        var hint = (context.ControlHint ?? "text").ToLowerInvariant();
        if (registry.Controls.TryGetValue(hint, out var control))
        {
            return control(context);
        }

        if (SchemaValueKinds.IsStructured(context.ValueKind))
        {
            return builder =>
            {
                builder.OpenComponent<HarborlineUnresolvedControl>(0);
                builder.AddAttribute(1, nameof(HarborlineUnresolvedControl.FieldName), context.FieldName);
                builder.AddAttribute(2, nameof(HarborlineUnresolvedControl.ControlHint), hint);
                builder.CloseComponent();
            };
        }

        if (!registry.Controls.TryGetValue("text", out var textFallback))
        {
            throw new InvalidOperationException(
                "The composed control registry has no 'text' control — the string-kind " +
                "fallback (ADR 0055 Rev 10 D-R10.5) requires it to survive composition.");
        }

        return textFallback(context);
    }
}
