# ArcGauge — Semantic Contract

- **Component:** ArcGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./ArcGauge.Interaction.md) · [Accessibility](./ArcGauge.Accessibility.md) · [Styling](./ArcGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik ArcGauge baseline)
- **Catalog row:** #A20 ArcGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ArcGauge baseline)

---

## 1. Component purpose

**ArcGauge** — a semi-circle arc gauge (no needle; arc fill indicates value). Visually simpler than CircularGauge; common in dashboard KPI cards.

---

## 2. Props (planned)

```typescript
interface ArcGaugeProps {
  value: number
  min?: number
  max?: number
  color?: string              // arc fill color; default: hsl(var(--primary))
  trackColor?: string         // unfilled arc color; default: hsl(var(--muted))
  strokeWidth?: number        // arc stroke width; default: 12
  startAngle?: number         // default: 180 (left)
  endAngle?: number           // default: 0 (right)
  centerLabel?: React.ReactNode
  width?: number | string
  height?: number
  className?: string
}
```

---

## 3. Relationship to CircularGauge

ArcGauge is a no-needle variant of CircularGauge with a simpler API. No range bands, no scale labels. Intended for single-metric KPI cards.
