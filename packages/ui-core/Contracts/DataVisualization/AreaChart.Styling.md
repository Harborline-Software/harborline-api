# AreaChart — Styling Contract

- **Component:** AreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./AreaChart.Semantic.md) · [Interaction](./AreaChart.Interaction.md) · [Accessibility](./AreaChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts AreaChart baseline)
- **Catalog row:** #A1 AreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Container

`w-full` (or fixed width via prop). No border.

---

## 2. Series colors

Default series palette: `hsl(var(--chart-1))` through `hsl(var(--chart-5))` — chart-specific CSS variables defined in the design token layer. When a `color` prop is set on a series, it overrides the palette slot.

---

## 3. Area fill

Fill: series color at `fillOpacity` (default 0.3). Stroke: series color at full opacity, `strokeWidth` (default 2px).

---

## 4. Grid lines

Horizontal dashed grid lines: `stroke: hsl(var(--border))` at `opacity: 0.5`.

---

## 5. Axes

Axis tick labels: `text-xs text-muted-foreground`. Axis lines: `stroke: hsl(var(--border))`. Axis labels: `text-xs font-medium text-foreground`.

---

## 6. Tooltip

`bg-popover text-popover-foreground border border-border rounded shadow text-xs p-2`. Series rows: color swatch `inline-block size-2 rounded-full mr-1` + series name + value.

---

## 7. Legend

`flex flex-wrap gap-3 mt-2 justify-center text-xs text-muted-foreground`. Color swatch: `inline-block size-2 rounded-full mr-1`.

---

## 8. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--border))`, `hsl(var(--muted-foreground))`, `hsl(var(--foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
