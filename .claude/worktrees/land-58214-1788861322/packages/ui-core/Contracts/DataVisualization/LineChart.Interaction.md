# LineChart — Interaction Contract

- **Component:** LineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./LineChart.Semantic.md) · [Accessibility](./LineChart.Accessibility.md) · [Styling](./LineChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts LineChart baseline)
- **Catalog row:** #A3 LineChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover tooltip

Vertical crosshair line tracks mouse position; tooltip shows all series values at the hovered x position.

---

## 2. Dot hover

When `dot=true`: hovered dot increases in size (visual feedback). No click event.

---

## 3. Legend interaction

Clicking legend item toggles series visibility.

---

## 4. Known gaps

None identified for forward-spec.
