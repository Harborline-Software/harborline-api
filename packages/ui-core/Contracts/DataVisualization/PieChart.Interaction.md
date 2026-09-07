# PieChart — Interaction Contract

- **Component:** PieChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./PieChart.Semantic.md) · [Accessibility](./PieChart.Accessibility.md) · [Styling](./PieChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart baseline)
- **Catalog row:** #A4 PieChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover tooltip

Hovering a segment shows a tooltip with segment name, value, and percentage of total.

---

## 2. Segment hover highlight

Active segment renders slightly enlarged (transform: scale or outerRadius +4px) and the others dim slightly.

---

## 3. Legend interaction

Clicking a legend item hides/shows the corresponding segment.

---

## 4. No click events

No `onSegmentClick` in v1. Read-only display.

---

## 5. Known gaps

None identified for forward-spec.
