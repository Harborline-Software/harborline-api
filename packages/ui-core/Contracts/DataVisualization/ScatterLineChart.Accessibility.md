# ScatterLineChart — Accessibility Contract

- **Component:** ScatterLineChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScatterLineChart.Semantic.md) · [Interaction](./ScatterLineChart.Interaction.md) · [Accessibility](./ScatterLineChart.Accessibility.md) · [Styling](./ScatterLineChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ScatterLineChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

Plain `<div>` with no ARIA role or label. The chart is semantically invisible to screen readers.

---

## 2. SVG / canvas

ECharts canvas/SVG has no accessibility attributes. X/Y coordinate data and series lines are opaque to assistive technology.

---

## 3. Data table fallback

No data table fallback implemented. X/Y coordinate pairs for each series are inaccessible to AT.

---

## 4. Tooltip accessibility

ECharts tooltip is pointer-driven and not keyboard-accessible.

---

## 5. Keyboard

Container is not focusable. No keyboard navigation.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SL-A1 | High | Root element has no ARIA role or label | Accepted-risk M1; add `role="img"` + `aria-label` at implementation hardening |
| G-SL-A2 | High | No data table fallback — x/y coordinate pairs inaccessible to AT | Accepted-risk M1; implement visually-hidden table (columns: series name, x, y) in M2 |
| G-SL-A3 | Medium | Tooltip is pointer-only | Accepted-risk M1; deferred to M3 |
| G-SL-A4 | Low | Color is the only series differentiator in multi-series view | Accepted-risk M1; add pattern fills or markers in M2 |
