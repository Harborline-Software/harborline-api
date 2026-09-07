# RadarColumnChart — Styling Contract

- **Component:** RadarColumnChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RadarColumnChart.Semantic.md) · [Interaction](./RadarColumnChart.Interaction.md) · [Accessibility](./RadarColumnChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A14 RadarColumnChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)
- **Aliases-canonical-ref:** [RadarAreaChart](./RadarAreaChart.Styling.md) _(link only; update manually when canonical changes)_

---

## 1. Radial bars

SVG trapezoid wedges radiating outward. Fill: `hsl(var(--chart-N))` at `opacity: 0.85`. Hover: `opacity: 1`, stroke highlight.

---

## 2. Grid, axis labels, tooltip, legend

Extends RadarAreaChart.Styling.md §2-5.

---

## 3. Design tokens

Same as RadarAreaChart.Styling.md §6.
