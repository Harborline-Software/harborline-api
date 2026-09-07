# FunnelChart — Semantic Contract

- **Component:** FunnelChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./FunnelChart.Interaction.md) · [Accessibility](./FunnelChart.Accessibility.md) · [Styling](./FunnelChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts FunnelChart / Telerik Funnel Chart baseline)
- **Catalog row:** #A30 FunnelChart (`app-priority: deferred`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Component purpose

**FunnelChart** — renders a series of horizontally centered bars of decreasing (or variable) width, representing conversion stages in a process (e.g. marketing funnel, sales pipeline). Each stage shows a value and optional conversion rate.

---

## 2. Props (planned)

```typescript
interface FunnelStage {
  name: string
  value: number
  color?: string
}

interface FunnelChartProps {
  data: FunnelStage[]         // ordered top-to-bottom; first is widest stage
  width?: number | string
  height?: number
  isSymmetric?: boolean       // symmetric trapezoid shape; default: true
  tooltip?: boolean
  label?: boolean | 'value' | 'percent' | 'both'  // default: 'both'
  orientation?: 'vertical' | 'horizontal'          // default: 'vertical'
  className?: string
}
```

---

## 3. Value vs percent labels

When `label='value'`: shows raw `value`. When `label='percent'`: shows each stage's value as a percentage of the first (top) stage. When `label='both'`: shows value + conversion % below it.

---

## 4. Ordering

Data is rendered in array order (top to bottom for `orientation='vertical'`). No automatic sorting — caller provides sorted stages.

---

## 5. PL3-18 disposition — data shape vs PL3-8 categorical family

FunnelChart and PyramidChart use a per-item `{ name: string; value: number }` object array rather than the `categoryKey + ChartDataPoint[]` flat-row shape used by the categorical chart family (BarChart, LineChart, AreaChart, ColumnChart). This is intentional and does not conflict with PL3-8:

- Categorical charts have a shared `data: ChartDataPoint[]` array where each row spans all series; a `categoryKey` selects the category label from that row. The axis is linear and shared.
- Funnel/Pyramid charts have no linear categorical axis — each segment is independent with its own `name` (label) and `value` (size). The per-item shape is the correct abstraction.

`name` serves as the segment label; `value` is always numeric. No `categoryKey` prop applies. No further alignment needed.

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FN1 | Low | `legend?: boolean \| ChartLegendProps` union type — only `legend?: boolean` is active; `ChartLegendProps` reserved for M2 legend customization | Fix-deferred M2 — per PL3-21; full union activates when `ChartLegendProps` interface is ratified in chart-family council |
