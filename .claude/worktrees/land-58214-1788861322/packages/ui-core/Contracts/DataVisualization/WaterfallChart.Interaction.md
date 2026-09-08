# WaterfallChart — Interaction Contract

- **Component:** WaterfallChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./WaterfallChart.Semantic.md) · [Accessibility](./WaterfallChart.Accessibility.md) · [Styling](./WaterfallChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A12 WaterfallChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Hover tooltip

Hovering a bar shows: item name, delta value, running total after this item. Active bar dims inactive bars.

---

## 2. No click / selection

Read-only in v1.

---

## 3. Known gaps

None identified for forward-spec.
