# PyramidChart — Accessibility Contract

- **Component:** PyramidChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PyramidChart.Semantic.md) · [Interaction](./PyramidChart.Interaction.md) · [Accessibility](./PyramidChart.Accessibility.md) · [Styling](./PyramidChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/PyramidChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

Plain `<div>` with no ARIA role or label. The pyramid chart is semantically invisible to screen readers.

---

## 2. SVG / canvas

ECharts canvas/SVG has no accessibility attributes. Pyramid segment data is opaque to assistive technology.

---

## 3. Data table fallback

No data table fallback implemented. Segment names and values are inaccessible to AT.

---

## 4. Tooltip accessibility

ECharts tooltip is pointer-driven and not keyboard-accessible.

---

## 5. `onItemClick` keyboard accessibility

`onItemClick` is wired to ECharts click events on rendered SVG/canvas elements. These elements are not keyboard-focusable. The click handler cannot be triggered via keyboard.

---

## 6. Keyboard

Container and chart elements are not keyboard-focusable. No keyboard navigation is implemented.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PYR-A1 | High | Root element has no ARIA role or label | Accepted-risk M1; add `role="img"` + `aria-label` at implementation hardening |
| G-PYR-A2 | High | No data table fallback — segment values inaccessible to AT | Accepted-risk M1; implement visually-hidden table in M2 |
| G-PYR-A3 | Medium | `onItemClick` is mouse-only — keyboard users cannot activate individual segments | Accepted-risk M1; keyboard segment navigation deferred to M3 |
| G-PYR-A4 | Low | Tooltip is pointer-only | Accepted-risk M1; keyboard tooltip deferred to M3 |
