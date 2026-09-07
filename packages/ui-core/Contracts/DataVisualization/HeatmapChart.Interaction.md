# HeatmapChart — Interaction Contract

- **Component:** HeatmapChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./HeatmapChart.Semantic.md) · [Accessibility](./HeatmapChart.Accessibility.md) · [Styling](./HeatmapChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A10 HeatmapChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Cell hover

Hovering a cell shows a tooltip with x label, y label, and numeric value. The hovered cell renders with a highlighted border.

---

## 2. No click

Read-only in v1.

---

## 3. Known gaps

None identified for forward-spec.
