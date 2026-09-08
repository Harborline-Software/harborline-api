# HeatmapChart — Styling Contract

- **Component:** HeatmapChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./HeatmapChart.Semantic.md) · [Interaction](./HeatmapChart.Interaction.md) · [Accessibility](./HeatmapChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A10 HeatmapChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Cells

SVG `<rect>` elements. Fill: interpolated between `colorScale[0]` and `colorScale[1]`. Default scale: `hsl(var(--chart-1))` at 10% opacity (low) → `hsl(var(--chart-1))` at 100% opacity (high). Gap between cells: 2px.

Hover: `stroke: hsl(var(--foreground))` `stroke-width: 1.5`.

Null cells: `fill: hsl(var(--muted))`.

---

## 2. Axis labels

`text-xs fill-muted-foreground`. X-labels below grid; Y-labels left of grid.

---

## 3. Color legend strip

`w-full h-3 rounded` gradient from `colorScale[0]` to `colorScale[1]`. Min/max labels: `text-xs text-muted-foreground`.

---

## 4. Tooltip

Same as AreaChart.Styling.md §6.

---

## 5. Design tokens

Uses: `hsl(var(--chart-1))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
