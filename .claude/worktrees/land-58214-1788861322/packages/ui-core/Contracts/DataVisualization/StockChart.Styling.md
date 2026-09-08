# StockChart — Styling Contract

- **Component:** StockChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./StockChart.Semantic.md) · [Interaction](./StockChart.Interaction.md) · [Accessibility](./StockChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A15 StockChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik StockChart baseline)

---

## 1. Candlestick

Up candle (close ≥ open): `fill: hsl(var(--chart-2))` (green-ish). Down candle: `fill: hsl(var(--destructive))`. Wick: `stroke: hsl(var(--foreground))` 1px.

OHLC type: tick lines same color rule.

---

## 2. Indicators

MA line: `hsl(var(--chart-4))`. EMA line: `hsl(var(--chart-5))`. Bollinger bands: `hsl(var(--muted-foreground))` dashed.

---

## 3. Volume bars

`fill: hsl(var(--muted-foreground))` at `opacity: 0.5`. Height auto-scaled to 20% of chart area.

---

## 4. Navigator

Mini chart area: `fill: hsl(var(--muted))`. Selection window: `fill: hsl(var(--primary))` at `opacity: 0.15`, border `hsl(var(--primary))`. Handle: `fill: hsl(var(--primary))` rounded rect.

---

## 5. Grid, crosshair, axes, tooltip

Same tokens as AreaChart.Styling.md §3-6. Crosshair: `stroke: hsl(var(--muted-foreground))` dashed.

---

## 6. Design tokens

Uses: `hsl(var(--chart-2..5))`, `hsl(var(--destructive))`, `hsl(var(--primary))`, `hsl(var(--muted))`, `hsl(var(--muted-foreground))`, `hsl(var(--foreground))`, `hsl(var(--border))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
