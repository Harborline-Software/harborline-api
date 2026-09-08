namespace Harborline.Api.Foundation.Definitions;

/// <summary>
/// A layer in the config cascade (annex §3.2: <c>base → pack → tenant → instance</c>).
/// Carried as <b>provenance</b> on every registry row and every per-field scoring metadata
/// entry so that overrides are always attributable and a downstream layer can never silently
/// defeat an upstream floor (F1). Ordered by increasing specificity; comparison uses the
/// enum's numeric order.
/// </summary>
public enum CascadeLayer
{
    /// <summary>Platform base seed — the most general, most authoritative floor.</summary>
    Base = 0,

    /// <summary>Pack-provided seed (e.g. the property pack ships Condenser, Panel, Water heater).</summary>
    Pack = 1,

    /// <summary>Tenant extension / override.</summary>
    Tenant = 2,

    /// <summary>Per-instance (per-entity / per-visit) override — the most specific layer.</summary>
    Instance = 3,
}

/// <summary>Helpers over <see cref="CascadeLayer"/>.</summary>
public static class CascadeLayerExtensions
{
    /// <summary>
    /// True when a value declared at <paramref name="floor"/> is a <b>seed</b> layer
    /// (<see cref="CascadeLayer.Base"/> or <see cref="CascadeLayer.Pack"/>) — the layers whose
    /// critical/safety floors are raise-strictness-only downstream (F1). A tenant/instance layer
    /// may raise strictness but never lower a seed-declared floor.
    /// </summary>
    public static bool IsSeed(this CascadeLayer floor) =>
        floor is CascadeLayer.Base or CascadeLayer.Pack;
}
