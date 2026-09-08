namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Inputs;

/// <summary>A flat option for <see cref="HarborlineSearchableSelect"/>. Options sharing a
/// <see cref="Group"/> render under a non-interactive group header.</summary>
public sealed record SearchableSelectOption(
    string Value,
    string Label,
    string? Group = null,
    bool Disabled = false);
