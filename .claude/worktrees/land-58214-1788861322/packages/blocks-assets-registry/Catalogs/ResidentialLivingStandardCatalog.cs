using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Model.Scoring;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Catalogs;

/// <summary>
/// A <b>real, modest pack-seeded living-standard catalog</b> — a residential inspection SLICE curated from
/// the ONR living-standard item catalog (regimes: NSPIRE / HQS / HHSRS / UAE-DM, 2026-07-02): 10 items
/// across 3 categories (safety / kitchen / bath) and 2 disciplines (electrical / plumbing), each with the
/// D-P per-field scoring aspect (weight, category, discipline, item kind, criticality, validity).
/// </summary>
/// <remarks>
/// <para>
/// This is the <see cref="StandardCatalogSeed"/> content (the scoring overlay + the condition-rating
/// bindings). The item-catalog FORM (schema + labels + sections) is registered by the node host that ships
/// this pack — the field pointers below are the single source both sides share. It is a curated <b>seed</b>
/// the CIC then edits on the cascade, not a straitjacket (ONR note §7).
/// </para>
/// <para>
/// <b>Raise-only safety floors (F1).</b> The four life-safety items (<see cref="ExposedWiring"/>,
/// <see cref="GasLeak"/> at <see cref="ScoringCriticality.SafetyCritical"/>; <see cref="KitchenGfci"/>,
/// <see cref="BathGfci"/> at <see cref="ScoringCriticality.Critical"/>) ship a Pack-declared floor that a
/// tenant overlay can raise but never lower — the D-R "critical items are never averaged away" invariant.
/// </para>
/// <para>
/// <b>Bindings.</b> The six RATING items bind (via the submission's <see cref="UnitRefField"/>) so a
/// submission of the catalog form projects a typed <see cref="ConditionAssessment"/> per rated item onto
/// the inspected unit. The four pass/fail safety items are scored by the Wave-3b compute (they carry no
/// condition-rating binding). NO composite is computed here (Rev 3.1 fold).
/// </para>
/// </remarks>
public static class ResidentialLivingStandardCatalog
{
    /// <summary>Stable catalog key (shared across tenants).</summary>
    public const string Key = "living-standard.residential.slice.v1";

    /// <summary>Human-readable catalog label (the localized FORM title lives on the form definition).</summary>
    public const string Title = "Residential living-standard inspection (slice)";

    /// <summary>The item-catalog form id (the FORM a host registers/publishes with these fields).</summary>
    public const string FormId = "living-standard.residential.slice.v1";

    /// <summary>The submission field carrying the inspected unit's entity id (the bindings' ref source).</summary>
    public const string UnitRefField = "unit_ref";

    // ── Item field pointers (the single source the form definition + the scoring overlay share) ─────────
    // Rating items (1..5) — condition-rating, bound.
    public const string PanelCondition = "panel_condition";   // ELE-01
    public const string OutletFunction = "outlet_function";   // ELE-02
    public const string KitchenSink = "kitchen_sink";         // KIT-05
    public const string RangeCondition = "range_condition";   // KIT-01
    public const string ToiletCondition = "toilet_condition"; // BATH-01
    public const string BathDrainage = "bath_drainage";       // BATH-11
    // Safety pass/fail items — scored, NOT condition-rating bound (Wave-3b compute).
    public const string ExposedWiring = "exposed_wiring";     // SAF-07 (life-safety)
    public const string GasLeak = "gas_leak";                 // SAF-03 (life-safety)
    public const string KitchenGfci = "kitchen_gfci";         // KIT-07 (critical)
    public const string BathGfci = "bath_gfci";               // BATH-08 (critical)

    /// <summary>The condition scale every rating item in this slice rates on (1..5).</summary>
    public const int ScaleMax = ConditionRating.DefaultScaleMax;

    private const string CatSafety = "safety";
    private const string CatKitchen = "kitchen";
    private const string CatBath = "bath";
    private const string DiscElectrical = "electrical";
    private const string DiscPlumbing = "plumbing";

    private static readonly TimeSpan OneYear = TimeSpan.FromDays(365);
    private static readonly TimeSpan TwoYears = TimeSpan.FromDays(730);

    /// <summary>The six condition-rating item field names (integer 1..5) — the bound, projected items.</summary>
    public static IReadOnlyList<string> RatingItems { get; } = new[]
    {
        PanelCondition, OutletFunction, KitchenSink, RangeCondition, ToiletCondition, BathDrainage,
    };

    /// <summary>The four pass/fail safety item field names (boolean) — scored, not condition-rating bound.</summary>
    public static IReadOnlyList<string> SafetyItems { get; } = new[]
    {
        ExposedWiring, GasLeak, KitchenGfci, BathGfci,
    };

    /// <summary>The pack-layer per-field scoring overlay (the D-P scoring aspect for every item).</summary>
    public static FieldScoringOverlay BuildScoringOverlay() => new(
        new FormDefinitionId(FormId),
        CascadeLayer.Pack,
        new[]
        {
            // Rating items (worst→best 1..5).
            Rating(PanelCondition, weight: 6, CatSafety, DiscElectrical, TwoYears),
            Rating(OutletFunction, weight: 5, CatSafety, DiscElectrical, OneYear),
            Rating(KitchenSink, weight: 6, CatKitchen, DiscPlumbing, OneYear),
            Rating(RangeCondition, weight: 6, CatKitchen, DiscElectrical, OneYear),
            Rating(ToiletCondition, weight: 7, CatBath, DiscPlumbing, OneYear),
            Rating(BathDrainage, weight: 4, CatBath, DiscPlumbing, OneYear),
            // Life-safety pass/fail — raise-only seed floors (F1).
            Safety(ExposedWiring, weight: 10, CatSafety, DiscElectrical, ScoringCriticality.SafetyCritical, OneYear),
            Safety(GasLeak, weight: 10, CatSafety, DiscPlumbing, ScoringCriticality.SafetyCritical, OneYear),
            Safety(KitchenGfci, weight: 7, CatKitchen, DiscElectrical, ScoringCriticality.Critical, OneYear),
            Safety(BathGfci, weight: 7, CatBath, DiscElectrical, ScoringCriticality.Critical, OneYear),
        });

    /// <summary>
    /// The condition-rating bindings — one per rating item, sourcing the inspected unit from the
    /// submission's <see cref="UnitRefField"/>, so a submit projects a typed condition record per item.
    /// </summary>
    public static IReadOnlyList<ConditionRatingFieldBinding> BuildBindings()
    {
        var form = new FormDefinitionId(FormId);
        return RatingItems
            .Select(item => new ConditionRatingFieldBinding(
                form, "/" + item, ConditionEntityRefSource.SubmissionField, UnitRefField, ScaleMax))
            .ToArray();
    }

    /// <summary>Builds the immutable pack catalog seed (scoring overlay + bindings).</summary>
    public static StandardCatalogSeed BuildSeed()
        => new(Key, Title, BuildScoringOverlay(), BuildBindings());

    private static FieldScoringMetadata Rating(
        string pointer, decimal weight, string category, DisciplineTag discipline, TimeSpan validity)
        => new("/" + pointer, weight, category, discipline, ScoringItemKind.Rating, ScoringCriticality.Normal,
            validity, MeasurementRange: null, CascadeLayer.Pack);

    private static FieldScoringMetadata Safety(
        string pointer, decimal weight, string category, DisciplineTag discipline,
        ScoringCriticality criticality, TimeSpan validity)
        => new("/" + pointer, weight, category, discipline, ScoringItemKind.SafetyPassFail, criticality,
            validity, MeasurementRange: null, CascadeLayer.Pack);
}
