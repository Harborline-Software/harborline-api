# ColumnChart — Styling Contract

- **Component:** ColumnChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./ColumnChart.Semantic.md) · [Interaction](./ColumnChart.Interaction.md) · [Accessibility](./ColumnChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts BarChart baseline)
- **Catalog row:** #A6 ColumnChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)
- **Aliases-canonical-ref:** [BarChart](./BarChart.Styling.md) _(link only; update manually when canonical changes)_

---

## 1. Styling

Extends BarChart — see `BarChart.Styling.md`.

Column bars extend vertically (bottom origin) rather than horizontally, but token usage and visual treatment are the same.

---

## 2. Design tokens

Same as BarChart.Styling.md §5.
