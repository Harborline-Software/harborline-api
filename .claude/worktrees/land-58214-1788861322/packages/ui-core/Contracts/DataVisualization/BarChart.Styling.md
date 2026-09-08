# BarChart — Styling Contract

- **Component:** BarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BarChart.Semantic.md) · [Interaction](./BarChart.Interaction.md) · [Accessibility](./BarChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts BarChart baseline)
- **Catalog row:** #A2 BarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Container

`w-full` (or fixed width). No border.

---

## 2. Bars

Fill: series palette `hsl(var(--chart-N))` or `color` override. Hover: `opacity: 0.8` on active bar; `opacity: 0.4` on inactive bars.

Bar radius: `2px` on bar tip corners (softens the look).

---

## 3. Grid, axes, tooltip, legend

Same tokens as AreaChart.Styling.md §3-7: `hsl(var(--border))`, `text-muted-foreground`, `bg-popover`, etc.

---

## 4. Bar labels

`text-xs fill-foreground` (for `outside`) or `text-xs fill-background` (for `inside` on dark bars).

---

## 5. Design tokens

Uses: `hsl(var(--chart-1..5))`, `hsl(var(--border))`, `hsl(var(--muted-foreground))`, `hsl(var(--foreground))`, `hsl(var(--background))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.
