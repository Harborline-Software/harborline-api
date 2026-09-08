namespace Harborline.Api.UIAdapters.Blazor.Components.Showcase;

/// <summary>
/// Localizable chrome labels for <see cref="HarborlineExamplePanel"/>.
/// Consumers that do not cascade an instance retain these English defaults.
/// </summary>
public sealed class HarborlineExamplePanelLabels
{
    public string Preview { get; init; } = "Preview";
    public string Controls { get; init; } = "Controls";
    public string Output { get; init; } = "Output";
    public string Code { get; init; } = "Code";
    public string CopyCode { get; init; } = "Copy code";
    public string CopyCodeSucceeded { get; init; } = "Copied";
    public string CopyCodeFailed { get; init; } = "Copy failed";
}
