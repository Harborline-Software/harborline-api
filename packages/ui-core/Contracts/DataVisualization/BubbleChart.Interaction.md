# BubbleChart — Interaction Contract

- **Component:** BubbleChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BubbleChart.Semantic.md) · [Accessibility](./BubbleChart.Accessibility.md) · [Styling](./BubbleChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A7 BubbleChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover tooltip

Hovering a bubble shows: series name, x value, y value, z value, and optional label. The hovered bubble renders with a highlighted stroke.

---

## 2. Legend interaction

Clicking a legend item hides/shows that series.

---

## 3. Known gaps

None identified for forward-spec.
