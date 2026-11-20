using Harborline.Api.Foundation.Packs.Model;
using Harborline.Foundation.Definitions;

namespace Harborline.Api.Foundation.Packs.Graph;

/// <summary>
/// The dev→customer projection the feature graph applies to each contributable: the pillar it groups under
/// and the DOMAIN-GENERIC customer noun for its kind. Both outputs are STABLE tokens (never localized copy)
/// — the Harborline App maps them to localized labels in the <c>apps</c> locale block (G8), so this map assumes no
/// business domain and no locale. The concrete per-item noun (a type's <c>displayName</c> — "Vehicle") is
/// app-supplied DATA carried on the node, not a token from here; domain vocabulary arrives only via the app.
/// </summary>
public static class PackPillarMap
{
    /// <summary>The customer pillar a content kind groups under (design note §2.1).</summary>
    public static PackPillar ForKind(PackContentKind kind)
    {
        foreach (var registeredKind in PackContentKindRegistry.Kinds)
        {
            if (registeredKind.Status == PackContentKindStatus.Shipped
                && registeredKind.Value == (int)kind)
            {
                return (PackPillar)PackContentKindRegistry.PillarOf(registeredKind.Name).Value;
            }
        }

        return PackPillar.Other;
    }

    /// <summary>
    /// The domain-generic customer noun TOKEN for a content kind ("recordType", "form", "automation", …) —
    /// a stable key the Harborline App localizes, never English copy. Used as the fallback label when a
    /// contributable carries no concrete <c>displayName</c> of its own.
    /// </summary>
    public static string GenericNoun(PackContentKind kind) => kind switch
    {
        PackContentKind.AssetTypeDefinition => "recordType",
        PackContentKind.FormDefinition => "form",
        PackContentKind.WorkflowDefinition => "automation",
        PackContentKind.NavWorkspaceConfig => "workspace",
        PackContentKind.StandardsCatalog => "standard",
        PackContentKind.StandingRuleDefinition => "standingRule",
        PackContentKind.CascadeDefaults => "default",
        PackContentKind.TerminologyOverride => "wording",
        PackContentKind.TemplateDefinition => "template",
        PackContentKind.TaxonomyDefinition => "taxonomy",
        PackContentKind.ReportDefinition => "report",
        PackContentKind.DataExchangeDefinition => "exchange",
        PackContentKind.Layout => "layout",
        PackContentKind.Resource => "resource",
        PackContentKind.Bookable => "bookable",
        PackContentKind.ScheduleDefinition => "schedule",
        PackContentKind.ViewDefinition => "view",
        _ => "contribution",
    };
}
