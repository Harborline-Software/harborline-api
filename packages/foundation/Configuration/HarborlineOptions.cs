namespace Harborline.Api.Foundation.Configuration;

/// <summary>
/// Configuration options for the Harborline component library, typically set during
/// service registration in <c>Program.cs</c> or <c>Startup.cs</c>.
/// </summary>
public class HarborlineOptions
{
    /// <summary>
    /// Gets or sets the initial theme applied when the application starts.
    /// When <c>null</c>, the provider's default theme is used.
    /// </summary>
    public HarborlineTheme? Theme { get; set; }
}
