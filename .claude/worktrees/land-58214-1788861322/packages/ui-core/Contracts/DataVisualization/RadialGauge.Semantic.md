# RadialGauge — Semantic Contract

- **Component:** RadialGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./RadialGauge.Interaction.md) · [Accessibility](./RadialGauge.Accessibility.md) · [Styling](./RadialGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik RadialGauge baseline)
- **Catalog row:** #A32 RadialGauge (`app-priority: deferred`, `library-scope: future-wave`) — under #66 Gauges umbrella
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik RadialGauge baseline)

---

## 1. Component purpose

**RadialGauge** — a circular gauge with a needle pointer, major/minor tick marks, and scale labels. More detailed than ArcGauge; analogous to an analog dial. Supports multiple concentric scale rings.

---

## 2. Props (planned)

```typescript
interface RadialGaugeProps extends CircularGaugeProps {
  // Inherits all CircularGaugeProps. See CircularGauge.Semantic.md for the full interface.
  // Key additions / overrides:
  majorTicks?: number        // number of major tick marks on the arc (default: 5)
  minorTicks?: number        // minor tick subdivisions between major ticks (default: 5)
  scaleRings?: number        // number of concentric scale rings (default: 1)
}
```

See [`CircularGauge.Semantic.md`](./CircularGauge.Semantic.md) for the base interface (`GaugeScale`, `GaugePointer`, `value`, `min`, `max`, etc.).

Key distinctions from CircularGauge:
- Tick marks rendered at major/minor intervals on the arc track.
- Multiple scale rings (inner/outer) supported.
- No center `innerRadius` hole by default (full disk background).

---

## 3. Tick marks

`GaugeScale.labels` drives major tick placement. Minor ticks auto-divide between major ticks at 5× resolution.

---

## 4. Relationship to CircularGauge

RadialGauge has higher visual fidelity (ticks, labels on arc). CircularGauge is simpler (arc fill or needle, no ticks). Use RadialGauge when dashboard requires instrument-panel aesthetics.
