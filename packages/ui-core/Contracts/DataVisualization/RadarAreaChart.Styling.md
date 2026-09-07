# RadarAreaChart — Styling Contract

- **Component:** RadarAreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RadarAreaChart.Semantic.md) · [Interaction](./RadarAreaChart.Interaction.md) · [Accessibility](./RadarAreaChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A13 RadarAreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Polygons

Fill: `hsl(var(--chart-N))` at `fillOpacity` (default 0.3). Stroke: `hsl(var(--chart-N))` full opacity, `stroke-width: 1.5`.

---

## 2. Grid

Concentric grid lines: `stroke: hsl(var(--border))` `opacity: 0.5`. Axis lines from center: `stroke: hsl(var(--border))`.

---

## 3. Axis labels

`text-xs fill-muted-foreground`. Positioned outside the outermost grid ring.

---

## 4. Dot markers

`<circle r="3">` at each axis point. Fill: series color. Stroke: `hsl(var(--background))` 1px.

---

## 5. Tooltip, legend

Same tokens as AreaChart.Styling.md §6-7.

---

## 6. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--border))`, `hsl(var(--background))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
