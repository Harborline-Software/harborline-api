namespace Harborline.Api.UIAdapters.Blazor.Components.Overlays;

/// <summary>A host-defined step in a <see cref="HarborlineTour"/>.</summary>
public sealed record HarborlineTourStep(
    string Title,
    string Description,
    string? TargetId = null);
