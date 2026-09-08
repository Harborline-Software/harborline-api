using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Harborline.Api.UIAdapters.Blazor.Base;
using Harborline.Api.UIAdapters.Blazor.Components.Forms.Containers;

namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Fields;

/// <summary>Shared composition and accessibility contract for field wrappers.</summary>
public abstract class HarborlineFieldBase : HarborlineComponentBase
{
    [Parameter] public string? Text { get; set; }
    [Parameter] public string? Id { get; set; }
    [Parameter] public string? AriaLabel { get; set; }
    [Parameter] public bool? Required { get; set; }
    [Parameter] public bool? Disabled { get; set; }
    [Parameter] public RenderFragment? Hint { get; set; }
    [Parameter] public RenderFragment? Error { get; set; }

    [CascadingParameter(Name = HarborlineField.RequiredCascadeName)]
    private bool CascadedRequired { get; set; }

    [CascadingParameter(Name = HarborlineField.DisabledCascadeName)]
    private bool CascadedDisabled { get; set; }

    [CascadingParameter] private EditContext? EditContext { get; set; }

    protected bool EffectiveRequired => Required ?? CascadedRequired;
    protected bool EffectiveDisabled => Disabled ?? CascadedDisabled;
    protected string FieldId => Id ?? $"harborline-field-{GetHashCode():x}";
    protected string? EffectiveAriaLabel => AriaLabel ?? (string.IsNullOrWhiteSpace(Text) ? null : Text);
    protected string AriaInvalid => HasError ? "true" : "false";
    protected string AriaRequired => EffectiveRequired ? "true" : "false";
    protected string? DescribedBy => string.Join(' ', new[] { HintId, ErrorId }.Where(id => !string.IsNullOrEmpty(id)));
    protected string? HintId => Hint is null ? null : $"{FieldId}-hint";
    protected string? ErrorId => Error is null ? null : $"{FieldId}-error";

    protected bool HasError
    {
        get
        {
            if (Error is not null)
            {
                return true;
            }

            return EditContext is not null
                && !string.IsNullOrEmpty(Id)
                && EditContext.GetValidationMessages(EditContext.Field(Id)).Any();
        }
    }
}
