# WaterfallChart — Semantic Contract

- **Component:** WaterfallChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./WaterfallChart.Interaction.md) · [Accessibility](./WaterfallChart.Accessibility.md) · [Styling](./WaterfallChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts ComposedChart waterfall pattern / Telerik Waterfall Chart baseline)
- **Catalog row:** #A12 WaterfallChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**WaterfallChart** — a column chart variant where bars float from the running total of prior bars. Used for cumulative financial analysis (e.g. bridge charts showing P&L walkforward, cash flow changes).

---

## 2. Props (planned)

```typescript
type WaterfallItemType = 'increase' | 'decrease' | 'total' | 'start'

interface WaterfallItem {
  name: string
  value: number                   // absolute value for 'start'/'total'; delta for 'increase'/'decrease'
  type?: WaterfallItemType        // default: auto-detected from value sign
  color?: string                  // override auto-color
}

interface WaterfallChartProps {
  data: WaterfallItem[]
  width?: number | string
  height?: number                 // default: 300
  tooltip?: boolean
  label?: boolean                 // show delta labels on bars; default: true
  connector?: boolean             // draw dotted connecting lines between bars; default: true
  className?: string
}
```

---

## 3. Bar positioning

Each bar's bottom edge starts at the running total of all prior items. `type='start'` and `type='total'` bars anchor to zero (render from y=0 to y=value). Positive deltas float upward; negative deltas float downward.

---

## 4. Auto-detection

When `type` is omitted: positive `value` → `'increase'`; negative `value` → `'decrease'`. The last item defaults to `'total'` if not specified by caller.
