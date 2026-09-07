# RadarAreaChart — Interaction Contract

- **Component:** RadarAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RadarAreaChart.Semantic.md) · [Accessibility](./RadarAreaChart.Accessibility.md) · [Styling](./RadarAreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A13 RadarAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover tooltip

Hovering the chart surface shows all series values for the nearest axis point.

---

## 2. Legend interaction

Clicking a legend item hides/shows that series polygon.

---

## 3. Known gaps

None identified for forward-spec.
