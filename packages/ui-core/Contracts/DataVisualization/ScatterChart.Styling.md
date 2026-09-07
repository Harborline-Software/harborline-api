# ScatterChart — Styling Contract

- **Component:** ScatterChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./ScatterChart.Semantic.md) · [Interaction](./ScatterChart.Interaction.md) · [Accessibility](./ScatterChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A8 ScatterChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts baseline)

---

## 1. Points

SVG shapes sized by `symbolSize`. Fill: `hsl(var(--chart-N))` or series `color` at `opacity: 0.8`. Hover: `opacity: 1`, stroke `hsl(var(--background))` 2px.

---

## 2. Reference lines

`stroke: hsl(var(--muted-foreground))` dashed `stroke-dasharray: 4 4`. Label: `text-xs fill-muted-foreground`.

---

## 3. Grid, axes, tooltip, legend

Same tokens as AreaChart.Styling.md §3-7.

---

## 4. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--border))`, `hsl(var(--background))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
