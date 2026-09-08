# BarChart — Interaction Contract

- **Component:** BarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BarChart.Semantic.md) · [Accessibility](./BarChart.Accessibility.md) · [Styling](./BarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts BarChart baseline)
- **Catalog row:** #A2 BarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover tooltip

When `tooltip=true`: hovering a bar shows a tooltip with the category label and all series values for that category row. Active bar is highlighted (increased opacity on others).

---

## 2. Legend interaction

Clicking a legend item toggles that series' visibility.

---

## 3. No click / selection

BarChart is read-only. No click events on bars in v1.

---

## 4. Known gaps

None identified for forward-spec. Validate tooltip positioning (especially for `layout='horizontal'`) against actual implementation in M2.
