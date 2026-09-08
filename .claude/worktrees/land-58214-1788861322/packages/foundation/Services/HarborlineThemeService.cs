using Harborline.Api.Foundation.Configuration;
using Harborline.Api.Foundation.Models;

namespace Harborline.Api.Foundation.Services;

/// <summary>
/// Framework-agnostic, in-memory implementation of <see cref="IHarborlineThemeService"/>.
/// Holds theme state and fires change events. Does not persist state across page loads.
/// The Blazor adapter (ui-adapters-blazor) provides a JS-backed implementation that
/// persists dark-mode preference to localStorage.
/// </summary>
public class HarborlineThemeService : IHarborlineThemeService
{
    private HarborlineTheme _currentTheme;

    /// <summary>
    /// Initializes the service with the supplied options. If no theme is configured,
    /// a default <see cref="HarborlineTheme"/> is used.
    /// </summary>
    public HarborlineThemeService(HarborlineOptions options)
    {
        _currentTheme = options.Theme ?? new HarborlineTheme();
    }

    /// <inheritdoc />
    public HarborlineTheme CurrentTheme => _currentTheme;

    /// <inheritdoc />
    public bool IsDarkMode { get; private set; }

    /// <inheritdoc />
    public bool IsRtl => _currentTheme.IsRtl;

    /// <inheritdoc />
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task SetThemeAsync(HarborlineTheme theme)
    {
        var oldTheme = _currentTheme;
        _currentTheme = theme;
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs
        {
            OldTheme = oldTheme,
            NewTheme = theme,
            IsDarkMode = IsDarkMode
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ToggleDarkModeAsync() => SetDarkModeAsync(!IsDarkMode);

    /// <inheritdoc />
    public Task SetDarkModeAsync(bool dark)
    {
        IsDarkMode = dark;
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs
        {
            OldTheme = _currentTheme,
            NewTheme = _currentTheme,
            IsDarkMode = IsDarkMode
        });
        return Task.CompletedTask;
    }
}
