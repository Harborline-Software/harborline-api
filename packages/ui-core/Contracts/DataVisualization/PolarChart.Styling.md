# PolarChart — Styling Contract

- **Component:** PolarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./PolarChart.Semantic.md) · [Interaction](./PolarChart.Interaction.md) · [Accessibility](./PolarChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A22 PolarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; ECharts polar/Recharts RadialBar baseline)

---

## 1. Container

Same as AreaChart.Styling.md §1: `w-full` + explicit `height` prop (default 300px).

---

## 2. Series colors

`hsl(var(--chart-1))` through `hsl(var(--chart-5))` cycling. Custom `series[i].color` overrides.

---

## 3. Sector / bar

Rose sectors: filled with series color, `stroke: hsl(var(--background))` `stroke-width: 1` for gap. Radial bars: `stroke-linecap: round`. Hover: brightness +10%.

---

## 4. Polar grid

Concentric circles: `stroke: hsl(var(--border))` `stroke-width: 1` `stroke-dasharray: 3 3`. Radial axis lines: `stroke: hsl(var(--border))` `stroke-width: 1`.

---

## 5. Labels

Angle-axis labels (sector names): `text-xs fill-muted-foreground`. Placed outside the outerRadius.

---

## 6. Design tokens

Same as AreaChart: `hsl(var(--chart-1..5))`, `hsl(var(--background))`, `hsl(var(--border))`, `hsl(var(--muted-foreground))`.
