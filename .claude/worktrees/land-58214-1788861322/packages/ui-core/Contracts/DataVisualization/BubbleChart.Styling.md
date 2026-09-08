# BubbleChart — Styling Contract

- **Component:** BubbleChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BubbleChart.Semantic.md) · [Interaction](./BubbleChart.Interaction.md) · [Accessibility](./BubbleChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A7 BubbleChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Bubbles

Fill: series palette `hsl(var(--chart-N))` or series/point `color` override at `opacity: 0.7`. Stroke: series color at full opacity, `stroke-width: 1.5`. Hover: stroke width `2.5`, `opacity: 0.9`.

---

## 2. Labels

`text-xs fill-foreground` centered on bubble.

---

## 3. Grid, axes, tooltip, legend

Same tokens as AreaChart.Styling.md §3-7.

---

## 4. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--border))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
