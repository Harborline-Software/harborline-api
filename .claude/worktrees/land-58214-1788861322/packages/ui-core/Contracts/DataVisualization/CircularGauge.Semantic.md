# CircularGauge — Semantic Contract

- **Component:** CircularGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./CircularGauge.Interaction.md) · [Accessibility](./CircularGauge.Accessibility.md) · [Styling](./CircularGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik CircularGauge baseline)
- **Catalog row:** #A18 CircularGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik CircularGauge baseline)

---

## 1. Component purpose

**CircularGauge** — a full-circle or arc gauge displaying a single numeric value against a range. Used for speedometers, performance scores, and KPI dials. The gauge needle or arc fill rotates to indicate the current value.

---

## 2. Props (planned)

```typescript
interface GaugeScale {
  min: number
  max: number
  labels?: { value: number; label: string }[]
  ranges?: { from: number; to: number; color: string }[]  // colored range bands on scale
}

interface CircularGaugeProps {
  value: number
  min?: number               // default: 0
  max?: number               // default: 100
  startAngle?: number        // default: -135 (7 o'clock)
  endAngle?: number          // default: 135 (5 o'clock)
  scale?: GaugeScale
  pointer?: 'needle' | 'arrow'  // default: 'needle'
  centerLabel?: React.ReactNode  // content at center (value text, unit)
  width?: number | string
  height?: number
  className?: string
}
```

---

## 3. Arc fill mode

Alternative to needle: a colored arc fills from `min` to `value`. Enabled when `pointer=undefined` and a color is set on the component (forward-spec: exact API TBD in M2).

---

## 4. Range bands

`GaugeScale.ranges` renders colored arc segments around the scale (e.g. red for low, green for high) before drawing the needle/pointer.
