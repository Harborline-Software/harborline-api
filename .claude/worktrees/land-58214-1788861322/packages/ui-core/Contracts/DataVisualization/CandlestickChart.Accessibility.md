# CandlestickChart — Accessibility Contract

- **Component:** CandlestickChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CandlestickChart.Semantic.md) · [Interaction](./CandlestickChart.Interaction.md) · [Accessibility](./CandlestickChart.Accessibility.md) · [Styling](./CandlestickChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/CandlestickChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

The component renders a plain `<div>` container with no ARIA role, label, or `aria-label`. There is no `role="img"` or `role="figure"` applied. Screen readers receive no semantic information about the chart's content from the root element.

---

## 2. SVG / canvas

ECharts renders to a `<canvas>` or SVG element inside the container div. Neither the canvas nor any SVG child carries an `aria-label`, `title`, or `aria-describedby` attribute. The chart is entirely opaque to assistive technology.

---

## 3. Data table fallback

No data table fallback is implemented. There is no visually-hidden table, `aria-live` region, or alternative text representation of the OHLC data.

---

## 4. Tooltip accessibility

The ECharts tooltip is pointer-driven. It is not keyboard-accessible and does not expose values to screen readers via ARIA live regions.

---

## 5. Keyboard

No keyboard navigation is implemented. The container div is not focusable.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CAND-A1 | High | Root element has no ARIA role or label — chart is invisible to screen readers | Accepted-risk M1; add `role="img"` + `aria-label` at implementation hardening |
| G-CAND-A2 | High | No data table fallback — OHLC values are inaccessible to AT | Accepted-risk M1; implement visually-hidden table in M2 |
| G-CAND-A3 | Medium | Tooltip is pointer-only; keyboard/SR users cannot inspect individual candle values | Accepted-risk M1; defer keyboard tooltip to M3 |
| G-CAND-A4 | Low | Color is the only rising/falling differentiator — colorblind users cannot distinguish candle direction without legend | Accepted-risk M1; add pattern or label overlays in M2 |
