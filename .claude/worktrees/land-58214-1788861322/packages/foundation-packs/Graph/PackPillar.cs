using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Graph;

/// <summary>
/// The customer's grouping bucket for an app's contributions (app-layer design note
/// <c>_shared/design/app-layer-feature-graph-2026-07-07.md</c> §2.1 kind→pillar map). A <b>pillar</b> is
/// the customer word (Records, Forms, Automations, …) for what an app added; the raw
/// <see cref="PackContentKind"/> enum is the dev word and is NEVER exposed to a customer surface (the
/// concept-lexicon rule — "never expose the raw kind enum to a customer"). This enum is a STABLE token the
/// Harborline App localizes; the read-model returns the token, the Apps surface (G3) maps it to a localized label
/// in the <c>apps</c> locale block. Domain-generic by construction (cerebrum 2026-07-07): a pillar names a
/// platform bucket, never a business domain.
/// </summary>
public enum PackPillar
{
    /// <summary>The Records pillar — data types the app added (<see cref="PackContentKind.AssetTypeDefinition"/>).</summary>
    Records = 0,

    /// <summary>The Forms pillar (<see cref="PackContentKind.FormDefinition"/>).</summary>
    Forms = 1,

    /// <summary>The Automations pillar (<see cref="PackContentKind.WorkflowDefinition"/>).</summary>
    Automations = 2,

    /// <summary>The Navigation pillar (<see cref="PackContentKind.NavWorkspaceConfig"/>).</summary>
    Navigation = 3,

    /// <summary>The Rules pillar — standards / safety floors (<see cref="PackContentKind.StandardsCatalog"/>).</summary>
    Rules = 4,

    /// <summary>The Settings pillar — cascade defaults + terminology
    /// (<see cref="PackContentKind.CascadeDefaults"/>, <see cref="PackContentKind.TerminologyOverride"/>).</summary>
    Settings = 5,

    /// <summary>The Documents pillar — document / merge templates (<see cref="PackContentKind.TemplateDefinition"/>,
    /// the Documents &amp; Templates pillar #111).</summary>
    Documents = 6,

    /// <summary>The Taxonomy pillar (<see cref="PackContentKind.TaxonomyDefinition"/>).</summary>
    Taxonomy = 7,

    /// <summary>The Reports pillar (<see cref="PackContentKind.ReportDefinition"/>).</summary>
    Reports = 8,

    /// <summary>The Data Exchange pillar (<see cref="PackContentKind.DataExchangeDefinition"/>).</summary>
    DataExchange = 11,

    /// <summary>The Scheduling pillar (<see cref="PackContentKind.ScheduleDefinition"/>).</summary>
    Scheduling = 10,

    /// <summary>The Views pillar (<see cref="PackContentKind.ViewDefinition"/>).</summary>
    Views = 9,

    /// <summary>A kind this node's map does not yet bucket — grouped generically rather than dropped
    /// (forward-compatible: an additive future kind still renders under a stable bucket).</summary>
    Other = 99,
}

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
    public static PackPillar ForKind(PackContentKind kind) => kind switch
    {
        PackContentKind.AssetTypeDefinition => PackPillar.Records,
        PackContentKind.FormDefinition => PackPillar.Forms,
        PackContentKind.WorkflowDefinition => PackPillar.Automations,
        PackContentKind.NavWorkspaceConfig => PackPillar.Navigation,
        PackContentKind.StandardsCatalog => PackPillar.Rules,
        PackContentKind.StandingRuleDefinition => PackPillar.Rules,
        PackContentKind.CascadeDefaults => PackPillar.Settings,
        PackContentKind.TerminologyOverride => PackPillar.Settings,
        PackContentKind.TemplateDefinition => PackPillar.Documents,
        PackContentKind.TaxonomyDefinition => PackPillar.Taxonomy,
        PackContentKind.ReportDefinition => PackPillar.Reports,
        PackContentKind.DataExchangeDefinition => PackPillar.DataExchange,
        PackContentKind.ScheduleDefinition => PackPillar.Scheduling,
        PackContentKind.ViewDefinition => PackPillar.Views,
        _ => PackPillar.Other,
    };

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
        PackContentKind.ScheduleDefinition => "schedule",
        PackContentKind.ViewDefinition => "view",
        _ => "contribution",
    };
}
