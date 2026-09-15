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
            "workshop.workspace" => Resources.GetString("workshop.workspace", CultureInfo.CurrentUICulture),
            "workshop.definitions" => Resources.GetString("workshop.definitions", CultureInfo.CurrentUICulture),
            "workshop.asset-types" => Resources.GetString("workshop.asset-types", CultureInfo.CurrentUICulture),
            "workshop.forms" => Resources.GetString("workshop.forms", CultureInfo.CurrentUICulture),
            "workshop.workflows" => Resources.GetString("workshop.workflows", CultureInfo.CurrentUICulture),
            "workshop.standards" => Resources.GetString("workshop.standards", CultureInfo.CurrentUICulture),
            "workshop.defaults" => Resources.GetString("workshop.defaults", CultureInfo.CurrentUICulture),
            "workshop.terminology" => Resources.GetString("workshop.terminology", CultureInfo.CurrentUICulture),
            "workshop.documents" => Resources.GetString("workshop.documents", CultureInfo.CurrentUICulture),
            "workshop.taxonomies" => Resources.GetString("workshop.taxonomies", CultureInfo.CurrentUICulture),
            "workshop.reports" => Resources.GetString("workshop.reports", CultureInfo.CurrentUICulture),
            "workshop.data-exchanges" => Resources.GetString("workshop.data-exchanges", CultureInfo.CurrentUICulture),
            "workshop.standing-rules" => Resources.GetString("workshop.standing-rules", CultureInfo.CurrentUICulture),
            "workshop.schedules" => Resources.GetString("workshop.schedules", CultureInfo.CurrentUICulture),
            "workshop.views" => Resources.GetString("workshop.views", CultureInfo.CurrentUICulture),
            _ => Resources.GetString(labelKey, CultureInfo.CurrentUICulture),
        }) ?? labelKey;
}
