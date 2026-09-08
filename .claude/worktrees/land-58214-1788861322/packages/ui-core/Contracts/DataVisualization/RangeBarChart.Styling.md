# RangeBarChart — Styling Contract

- **Component:** RangeBarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./RangeBarChart.Semantic.md) · [Interaction](./RangeBarChart.Interaction.md) · [Accessibility](./RangeBarChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A17 RangeBarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Floating bars

SVG `<rect>` starting at `from` position, ending at `to` position. Fill: `hsl(var(--chart-1))` or `RangeBarDataPoint.color` override. Bar radius: `2px`. Hover: `opacity: 0.85`, stroke highlight.

---

## 2. Labels

`text-xs fill-foreground` inside bar (centered) when bar width permits.

---

## 3. Grid, axes, tooltip

Same tokens as AreaChart.Styling.md §3-6.

---

## 4. Design tokens

Same as AreaChart.Styling.md §8.
