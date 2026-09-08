# Chart — Accessibility Contract

- **Component:** Chart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Chart.Semantic.md) · [Interaction](./Chart.Interaction.md) · [Accessibility](./Chart.Accessibility.md) · [Styling](./Chart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Chart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

The component renders a plain `<div>` with no ARIA role, `aria-label`, or `aria-describedby`. The chart is semantically invisible to screen readers.

---

## 2. SVG / canvas

ECharts renders to a `<canvas>` or SVG element inside the container. Neither element carries accessibility attributes. Chart content is opaque to assistive technology.

---

## 3. Data table fallback

No data table fallback is implemented. There is no visually-hidden `<table>`, `aria-live` region, or text summary of the chart data.

---

## 4. Tooltip accessibility

The ECharts tooltip is pointer-driven and not keyboard-accessible. Screen readers cannot access tooltip content.

---

## 5. Legend

ECharts renders a legend when `legend` is truthy. The legend is an ECharts-managed SVG/DOM structure with no explicit ARIA labeling.

---

## 6. Keyboard

The container div is not focusable. No keyboard navigation is implemented for any chart type.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CHART-A1 | High | Root element has no ARIA role or label — chart is invisible to screen readers regardless of `type` | Accepted-risk M1; add `role="img"` + dynamic `aria-label` based on `title` prop at implementation hardening |
| G-CHART-A2 | High | No data table fallback for any supported chart type | Accepted-risk M1; implement in M2 |
| G-CHART-A3 | Medium | Tooltip is pointer-only across all 13 chart types | Accepted-risk M1; keyboard tooltip deferred to M3 |
| G-CHART-A4 | Low | Color is the only series differentiator for multi-series Cartesian charts; colorblind users rely on legend alone | Accepted-risk M1; add pattern fills or markers in M2 |
