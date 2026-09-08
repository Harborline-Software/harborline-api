# RangeAreaChart — Interaction Contract

- **Component:** RangeAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RangeAreaChart.Semantic.md) · [Accessibility](./RangeAreaChart.Accessibility.md) · [Styling](./RangeAreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A16 RangeAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Hover tooltip

Hovering shows x label, low value, high value, and range width (high − low) for the nearest x position.

---

## 2. Legend interaction

Clicking a legend item hides/shows that series band.

---

## 3. Known gaps

None identified for forward-spec.
