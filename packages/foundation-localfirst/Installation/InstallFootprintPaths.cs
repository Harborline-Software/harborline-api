namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>Resolves the current user's platform-conventional Harborline data root.</summary>
public static class InstallFootprintPaths
{
    /// <summary>
    /// Gets the product-data directory that contains both the historical footprint and per-install roots.
    /// </summary>
    /// <returns>The current user's platform-specific Harborline data directory.</returns>
    public static string GetDefaultProductDataDirectory()
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

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataRoot = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share")
            : xdgDataHome;
        return Path.Combine(dataRoot, "sunfish");
    }

    internal static string GetLegacyDataDirectory()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return Path.Combine(GetDefaultProductDataDirectory(), "LocalNode");
        }

        return Path.Combine(GetDefaultProductDataDirectory(), "local-node");
    }

    internal static string GetLegacyDatabasePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(root, "Sunfish", "data", "sunfish.db");
    }

    internal static string GetLegacyKeystoreDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunfish",
            "keys");
}
