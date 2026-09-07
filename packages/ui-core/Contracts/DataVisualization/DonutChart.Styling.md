# DonutChart — Styling Contract

- **Component:** DonutChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./DonutChart.Semantic.md) · [Interaction](./DonutChart.Interaction.md) · [Accessibility](./DonutChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart baseline)
- **Catalog row:** #A5 DonutChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)
- **Aliases-canonical-ref:** [PieChart](./PieChart.Styling.md) _(link only; update manually when canonical changes)_

---

## 1. Styling

Extends PieChart — see `PieChart.Styling.md`.

---

## 2. Center label

Positioned with `position: absolute` over the SVG center using a CSS overlay approach, or rendered as SVG `<text>` elements. Primary value: `text-2xl font-bold text-foreground`. Sub-label: `text-xs text-muted-foreground`.

---

## 3. Design tokens

Same as PieChart.Styling.md §5.
