namespace Harborline.Api.UIAdapters.Blazor.Components.Showcase;

/// <summary>
/// A single source file exposed by <see cref="HarborlineExamplePanel"/>'s VIEW SOURCE tab.
/// </summary>
/// <param name="Name">
/// File name as displayed on the sub-tab (e.g. <c>Demo.razor</c>, <c>ProductService.cs</c>).
/// </param>
/// <param name="Content">The file's raw text content, as-is, for syntax-highlighted display.</param>
public sealed record HarborlineSourceFile(string Name, string Content);
