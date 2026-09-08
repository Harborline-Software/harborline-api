using Harborline.Api.Foundation.Configuration;

namespace Harborline.Api.Foundation.Models;

public class ThemeChangedEventArgs : EventArgs
{
    public required HarborlineTheme OldTheme { get; init; }
    public required HarborlineTheme NewTheme { get; init; }
    public bool IsDarkMode { get; init; }
}
