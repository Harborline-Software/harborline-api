# DonutChart — Accessibility Contract

- **Component:** DonutChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./DonutChart.Semantic.md) · [Interaction](./DonutChart.Interaction.md) · [Styling](./DonutChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart baseline)
- **Catalog row:** #A5 DonutChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)
- **Aliases-canonical-ref:** [PieChart](./PieChart.Accessibility.md) _(link only; update manually when canonical changes)_

---

## 1. Accessibility

Extends PieChart — see `PieChart.Accessibility.md`.

---

## 2. Center label accessibility

`centerLabel` renders inside the SVG `<figure>`. If it contains meaningful text (e.g. a total), that text must also appear in the `aria-label` on the `<figure>` or the data-table caption.

---

## 3. Known gaps

Same as PieChart (G-PIE-A1, G-PIE-A2).
