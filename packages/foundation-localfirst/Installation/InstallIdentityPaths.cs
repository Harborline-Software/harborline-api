using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>Resolves durable per-install identity-record locations.</summary>
public static class InstallIdentityPaths
{
    /// <summary>
    /// Gets the user-writable identity-record path for an installation directory.
    /// </summary>
    /// <param name="installRootDirectory">
    /// Stable installation directory retained by an in-place upgrade. Defaults to
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <returns>A path in the current user's application-state directory.</returns>
    public static string GetDefaultIdentityFilePath(string? installRootDirectory = null)
    {
        var fullInstallRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installRootDirectory ?? AppContext.BaseDirectory));
        if (OperatingSystem.IsWindows())
        {
            fullInstallRoot = fullInstallRoot.ToUpperInvariant();
        }

        var locator = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullInstallRoot)));

        return Path.Combine(GetUserStateDirectory(), "install-identities", locator + ".identity");
    }

    private static string GetUserStateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sunfish");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Sunfish");
        }

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var stateRoot = string.IsNullOrWhiteSpace(xdgStateHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "state")
            : xdgStateHome;
        return Path.Combine(stateRoot, "sunfish");
    }
}
