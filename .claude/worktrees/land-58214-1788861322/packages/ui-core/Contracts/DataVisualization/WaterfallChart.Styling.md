# WaterfallChart — Styling Contract

- **Component:** WaterfallChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./WaterfallChart.Semantic.md) · [Interaction](./WaterfallChart.Interaction.md) · [Accessibility](./WaterfallChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A12 WaterfallChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Bar colors

Increase bars: `hsl(var(--chart-2))` (typically green in the theme). Decrease bars: `hsl(var(--destructive))` (red). Start/Total bars: `hsl(var(--chart-1))` (neutral/primary). Override with `WaterfallItem.color`.

---

## 2. Connector lines

Dashed horizontal lines between bar tops: `stroke: hsl(var(--muted-foreground))` `stroke-dasharray: 3 3`.

---

## 3. Bar labels

`text-xs fill-foreground` above each bar showing the delta value with sign (`+12k`, `-4k`).

---

## 4. Grid, axes, tooltip

Same tokens as AreaChart.Styling.md §3-6.

---

## 5. Design tokens

Uses: `hsl(var(--chart-1))`, `hsl(var(--chart-2))`, `hsl(var(--destructive))`, `hsl(var(--muted-foreground))`, `hsl(var(--foreground))`, `hsl(var(--border))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
