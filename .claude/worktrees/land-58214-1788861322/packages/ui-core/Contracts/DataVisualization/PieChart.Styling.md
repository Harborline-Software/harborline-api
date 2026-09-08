# PieChart — Styling Contract

- **Component:** PieChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./PieChart.Semantic.md) · [Interaction](./PieChart.Interaction.md) · [Accessibility](./PieChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts PieChart baseline)
- **Catalog row:** #A4 PieChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Segments

Fill: `hsl(var(--chart-N))` palette or `PieSegment.color`. Stroke: `hsl(var(--background))` 2px (segment separator).

Active segment: `outerRadius + 4px` (expand-on-hover animation). Inactive segments: `opacity: 0.7`.

---

## 2. Labels

`text-xs fill-foreground` rendered outside slices. Line connecting label to slice: `stroke: hsl(var(--muted-foreground))`.

---

## 3. Center label (donut)

Absolutely centered text. Typically `text-2xl font-bold text-foreground` for numeric value, `text-xs text-muted-foreground` for subtitle.

---

## 4. Tooltip, legend

Same tokens as AreaChart.Styling.md §6-7.

---

## 5. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--background))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
