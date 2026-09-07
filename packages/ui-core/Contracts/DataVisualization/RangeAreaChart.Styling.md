# RangeAreaChart — Styling Contract

- **Component:** RangeAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RangeAreaChart.Semantic.md) · [Interaction](./RangeAreaChart.Interaction.md) · [Accessibility](./RangeAreaChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A16 RangeAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Band

Fill: series palette color at `fillOpacity` (default 0.3). Upper/lower boundary strokes: series color full opacity, `stroke-width: 1.5`.

---

## 2. Grid, axes, tooltip, legend

Same tokens as AreaChart.Styling.md §3-7.

---

## 3. Design tokens

Same as AreaChart.Styling.md §8.
