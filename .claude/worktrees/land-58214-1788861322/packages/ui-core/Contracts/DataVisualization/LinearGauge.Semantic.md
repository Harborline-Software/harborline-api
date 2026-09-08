# LinearGauge — Semantic Contract

- **Component:** LinearGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./LinearGauge.Interaction.md) · [Accessibility](./LinearGauge.Accessibility.md) · [Styling](./LinearGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik LinearGauge baseline)
- **Catalog row:** #A19 LinearGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik LinearGauge baseline)

---

## 1. Component purpose

**LinearGauge** — a horizontal or vertical bar gauge displaying a value along a linear scale. Simpler than CircularGauge for showing a fill-to-value metric (e.g. battery level, download progress with range context).

---

## 2. Props (planned)

```typescript
interface LinearGaugeProps {
  value: number
  min?: number               // default: 0
  max?: number               // default: 100
  orientation?: 'horizontal' | 'vertical'  // default: 'horizontal'
  scale?: GaugeScale         // see [CircularGauge.Semantic.md](./CircularGauge.Semantic.md)
  showLabel?: boolean        // show value label; default: true
  width?: number | string
  height?: number
  className?: string
}
```

---

## 3. Fill direction

`orientation='horizontal'`: fill grows left-to-right. `orientation='vertical'`: fill grows bottom-to-top.

---

## 4. Relationship to ProgressBar

LinearGauge adds range bands and scale labels not present in ProgressBar. ProgressBar is the simpler 0→100% component. LinearGauge is for domain-specific gauges with qualitative zones.
