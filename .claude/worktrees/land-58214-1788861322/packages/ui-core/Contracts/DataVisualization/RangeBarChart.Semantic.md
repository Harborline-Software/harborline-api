# RangeBarChart — Semantic Contract

- **Component:** RangeBarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./RangeBarChart.Interaction.md) · [Accessibility](./RangeBarChart.Accessibility.md) · [Styling](./RangeBarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Range Bar Chart / Gantt-bar pattern baseline)
- **Catalog row:** #A17 RangeBarChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik baseline)

---

## 1. Component purpose

**RangeBarChart** — a bar chart where each bar extends from a `from` value to a `to` value on the value axis (rather than from zero). Used for scheduling timelines, project durations, temperature ranges. Horizontal orientation is canonical for scheduling use cases.

---

## 2. Props (planned)

```typescript
interface RangeBarDataPoint {
  category: string
  from: number           // bar start value
  to: number             // bar end value
  label?: string
  color?: string
}

interface RangeBarChartProps {
  data: RangeBarDataPoint[]
  orientation?: 'horizontal' | 'vertical'  // default: 'horizontal'
  width?: number | string
  height?: number
  tooltip?: boolean
  grid?: boolean
  className?: string
}
```

---

## 3. Floating bars

Bars do not anchor to zero — they float between `from` and `to` on the value axis. This is the key semantic difference from a standard BarChart.

---

## 4. Gantt-lite use case

RangeBarChart with time values as `from`/`to` renders a lightweight Gantt-style timeline. For full project management Gantt features (dependencies, milestones), use the Gantt component.
