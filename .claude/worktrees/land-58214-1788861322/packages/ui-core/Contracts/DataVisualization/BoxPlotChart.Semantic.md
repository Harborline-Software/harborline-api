# BoxPlotChart — Semantic Contract

- **Component:** BoxPlotChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Draft
- **Companion contracts:** [Interaction](./BoxPlotChart.Interaction.md) · [Styling](./BoxPlotChart.Styling.md) · [Accessibility](./BoxPlotChart.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/BoxPlotChart.tsx`

---

## 1. Purpose

TODO: describe what BoxPlotChart is and the primary use cases it serves.

## 2. Data model

```typescript
export interface BoxPlotChartProps {
  /** Visual variant. */
  variant?: 'default'
  children?: React.ReactNode
}
```

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'default'` | `'default'` | TODO |
| `children` | `ReactNode` | — | TODO |

## 4. Events

TODO: enumerate callbacks and their payloads (or state "none in v1").

## 5. Slots

TODO: named sub-components / slot props (or "none — single children slot").

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/charts/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
