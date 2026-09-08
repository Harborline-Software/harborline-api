# OHLCChart — Accessibility Contract

- **Component:** OHLCChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./OHLCChart.Semantic.md) · [Interaction](./OHLCChart.Interaction.md) · [Accessibility](./OHLCChart.Accessibility.md) · [Styling](./OHLCChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/OHLCChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

Plain `<div>` with no ARIA role, `aria-label`, or `aria-describedby`. The chart is semantically invisible to screen readers. Identical situation to `CandlestickChart`.

---

## 2. SVG / canvas

ECharts canvas/SVG has no accessibility attributes. OHLC data is opaque to assistive technology.

---

## 3. Data table fallback

No data table fallback implemented.

---

## 4. Tooltip accessibility

Tooltip is pointer-driven, not keyboard-accessible, and does not surface values to screen readers.

---

## 5. Keyboard

Container is not focusable. No keyboard navigation.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-OHLC-A1 | High | Root element has no ARIA role or label | Accepted-risk M1; add `role="img"` + `aria-label` at implementation hardening |
| G-OHLC-A2 | High | No data table fallback — OHLC values inaccessible to AT | Accepted-risk M1; implement visually-hidden table in M2 |
| G-OHLC-A3 | Medium | Tooltip is pointer-only | Accepted-risk M1; keyboard tooltip deferred to M3 |
| G-OHLC-A4 | Low | Color is the only rising/falling differentiator | Accepted-risk M1; add pattern or label overlays in M2 |
