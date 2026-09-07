# RadarColumnChart — Semantic Contract

- **Component:** RadarColumnChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./RadarColumnChart.Interaction.md) · [Accessibility](./RadarColumnChart.Accessibility.md) · [Styling](./RadarColumnChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Radar Column Chart baseline)
- **Catalog row:** #A14 RadarColumnChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Component purpose

**RadarColumnChart** — a polar bar chart variant where bars radiate outward from the center, one bar per axis. Each bar's length encodes its value. Useful when categorical comparison is more important than the aggregate shape (vs RadarAreaChart).

---

## 2. Props (planned)

Same interface as `RadarAreaChart` with bars instead of filled polygons. See `RadarAreaChart.Semantic.md` for the prop interface.

Key difference: renders SVG `<rect>` bars (trapezoid wedges) from center outward rather than filled polygon overlays.

---

## 3. Multi-series

Multiple series render grouped radial bars (side by side on each axis, like grouped ColumnChart in polar).

---

## 4. Relationship to RadarAreaChart

RadarColumnChart shares the same polar grid layout as RadarAreaChart. Props are structurally identical. Exists as a named export for the bar-vs-area choice.
