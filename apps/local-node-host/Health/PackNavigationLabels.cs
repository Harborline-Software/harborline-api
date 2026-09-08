using System.Globalization;
using System.Resources;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Uses the API's embedded .resx resource path and culture fallback for pack rail labels.</summary>
internal static class PackNavigationLabels
{
    private static readonly ResourceManager Resources = new(
        "Harborline.Api.LocalNodeHost.Resources.Localization.PackNavigationLabels",
        typeof(PackNavigationLabels).Assembly);

    internal static string Resolve(string labelKey)
        => (labelKey switch
        {
            "access.holders" => Resources.GetString("access.holders", CultureInfo.CurrentUICulture),
            "navigation.asset-tree" => Resources.GetString("navigation.asset-tree", CultureInfo.CurrentUICulture),
            "navigation.invoices" => Resources.GetString("navigation.invoices", CultureInfo.CurrentUICulture),
            "navigation.documents" => Resources.GetString("navigation.documents", CultureInfo.CurrentUICulture),
            _ => Resources.GetString(labelKey, CultureInfo.CurrentUICulture),
        }) ?? labelKey;
}
