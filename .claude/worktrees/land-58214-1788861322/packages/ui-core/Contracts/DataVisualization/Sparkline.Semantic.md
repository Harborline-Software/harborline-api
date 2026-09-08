# Sparkline — Semantic Contract

- **Component:** Sparkline
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Sparkline.Interaction.md) · [Accessibility](./Sparkline.Accessibility.md) · [Styling](./Sparkline.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Sparkline / KendoReact Sparkline baseline)
- **Catalog row:** #120 Sparkline (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Sparkline baseline)

---

## 1. Component purpose

**Sparkline** — a compact inline chart used to visualize a data trend within a table cell, card, or dashboard summary widget. Renders a small SVG with no axes, labels, or legends. Supports line, bar, area, pie, and bullet subtypes.

---

## 2. Props (planned)

```typescript
type SparklineType = 'line' | 'bar' | 'area' | 'pie' | 'bullet'

interface SparklineProps {
  data: number[]                        // ordered data values
  type?: SparklineType                  // default: 'line'
  width?: number | string               // default: 100% of container
  height?: number                       // default: 30
  color?: string                        // default: primary token
  negativeColor?: string                // color for negative bars
  min?: number                          // y-axis domain min
  max?: number                          // y-axis domain max
  smooth?: boolean                      // smooth line curve; default: false
  markers?: boolean                     // show data-point dots; default: false
  markerSize?: number                   // dot radius; default: 3
  tooltip?: boolean                     // show hover tooltip; default: false
  tooltipRender?: (value: number, index: number) => React.ReactNode
  lineWidth?: number                    // stroke width for line/area; default: 1.5
  className?: string
  'aria-label'?: string                 // required for accessibility
}
```

---

## 3. Subtypes

**line**: Polyline connecting data points. `smooth` prop enables Catmull-Rom curve.

**area**: Filled area under a line. Fill uses `color` at reduced opacity.

**bar**: Vertical bars, one per data value. Negative values render in `negativeColor`.

**pie**: Small pie/donut chart. `data` values are segment proportions (no key/label).

**bullet**: Horizontal comparison bar showing actual vs target (requires `data` length ≥ 2: `[actual, target]`).

---

## 4. Sizing

Default renders at full container width. `height` controls SVG height (px). When embedded in a table cell, a fixed `width` (px) is recommended to prevent layout shift.

---

## 5. Tooltip

When `tooltip=true`, a floating label appears on hover showing the hovered data point's value. Position: above the hovered bar/point. `tooltipRender` replaces the default value string.

---

## 6. Timezone

Not applicable — Sparkline renders numeric data, not time-based axes.
