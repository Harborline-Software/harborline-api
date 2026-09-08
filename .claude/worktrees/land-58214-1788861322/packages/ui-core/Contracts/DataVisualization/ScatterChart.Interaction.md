# ScatterChart — Interaction Contract

- **Component:** ScatterChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./ScatterChart.Semantic.md) · [Accessibility](./ScatterChart.Accessibility.md) · [Styling](./ScatterChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A8 ScatterChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts baseline)

---

## 1. Point hover

Hovering near a point shows a tooltip with x, y values and optional label. The nearest point within a threshold distance is highlighted.

---

## 2. Legend interaction

Clicking a legend item toggles series visibility.

---

## 3. Known gaps

None identified for forward-spec.
