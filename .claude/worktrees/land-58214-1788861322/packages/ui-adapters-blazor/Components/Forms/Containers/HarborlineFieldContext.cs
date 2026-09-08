namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Containers;

/// <summary>
/// The state a field container makes available to composed controls.
/// </summary>
public sealed record HarborlineFieldContext(
    string? Id,
    bool Required,
    bool Disabled,
    bool Invalid,
    string? DescribedBy);
