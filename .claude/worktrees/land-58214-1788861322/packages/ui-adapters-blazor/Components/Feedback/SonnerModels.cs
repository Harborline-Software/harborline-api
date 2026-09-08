namespace Harborline.Api.UIAdapters.Blazor.Components.Feedback;

/// <summary>Semantic toast variant used by <see cref="HarborlineSonner"/>.</summary>
public enum SonnerToastVariant
{
    Default,
    Success,
    Error,
    Warning,
    Info,
    Loading,
}

/// <summary>A host-owned toast entry. The host controls creation and persistence.</summary>
public sealed record SonnerToastItem(
    string Id,
    string Message,
    SonnerToastVariant Variant = SonnerToastVariant.Default,
    string? Description = null,
    string? ActionLabel = null);
