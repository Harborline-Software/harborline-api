# RadarChart — Accessibility Contract

- **Component:** RadarChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RadarChart.Semantic.md) · [Interaction](./RadarChart.Interaction.md) · [Accessibility](./RadarChart.Accessibility.md) · [Styling](./RadarChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/RadarChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

Plain `<div>` with no ARIA role or label. The radar chart is semantically invisible to screen readers.

---

## 2. SVG / canvas

ECharts canvas/SVG has no accessibility attributes. Radar polygon data and axis labels are opaque to assistive technology.

---

## 3. Data table fallback

No data table fallback implemented. Indicator names, max values, and per-series values are inaccessible to AT.

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
| G-RADAR-A1 | High | Root element has no ARIA role or label | Accepted-risk M1; add `role="img"` + `aria-label` at implementation hardening |
| G-RADAR-A2 | High | No data table fallback — multi-series multi-axis values inaccessible to AT | Accepted-risk M1; implement visually-hidden table (rows=series, columns=indicators) in M2 |
| G-RADAR-A3 | Medium | Tooltip is pointer-only | Accepted-risk M1; deferred to M3 |
| G-RADAR-A4 | Low | Color is the only series differentiator in the polygon overlay; colorblind users rely on legend names | Accepted-risk M1; add pattern fills in M2 |
