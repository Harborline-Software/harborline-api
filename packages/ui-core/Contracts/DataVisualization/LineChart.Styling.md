# LineChart — Styling Contract

- **Component:** LineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./LineChart.Semantic.md) · [Interaction](./LineChart.Interaction.md) · [Accessibility](./LineChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts LineChart baseline)
- **Catalog row:** #A3 LineChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Lines

Stroke: series palette `hsl(var(--chart-N))` or `color` override. `stroke-width`: `strokeWidth` prop (default 2px). Dashed: `stroke-dasharray` prop.

---

## 2. Crosshair

Vertical `stroke: hsl(var(--border))` `opacity: 0.5` tracking mouse x.

---

## 3. Dots

`<circle>` fill: series color. Hover size: `r * 1.5`. Stroke: `hsl(var(--background))` 2px.

---

## 4. Grid, axes, tooltip, legend

Same tokens as AreaChart.Styling.md §3-7.

---

## 5. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--border))`, `hsl(var(--background))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
