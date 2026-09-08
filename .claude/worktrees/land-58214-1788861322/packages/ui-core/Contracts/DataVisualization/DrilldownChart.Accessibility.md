# DrilldownChart — Accessibility Contract

- **Component:** DrilldownChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DrilldownChart.Semantic.md) · [Interaction](./DrilldownChart.Interaction.md) · [Accessibility](./DrilldownChart.Accessibility.md) · [Styling](./DrilldownChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/DrilldownChart.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

The outer `<div className="relative">` has no ARIA role or label. The chart is semantically invisible to screen readers.

---

## 2. Chart canvas / SVG

ECharts renders to a canvas or SVG inside the container. Neither element carries `aria-label`, `title`, or `aria-describedby`. Chart content (bars, labels, axes) is opaque to assistive technology.

---

## 3. Back button

The "← Back" button is a native `<button>` element and is therefore keyboard-focusable and activatable via Enter/Space. It has no `aria-label` beyond its visible text content "← Back". Screen readers will announce "back button" but cannot convey the current drill level or what navigating back will display.

---

## 4. Data table fallback

No data table fallback is implemented. There is no visually-hidden table, `aria-live` region, or text summary of chart data at any drill level.

---

## 5. Tooltip accessibility

ECharts tooltip is pointer-driven and not accessible to screen readers or keyboard users.

---

## 6. Level change announcements

When the user drills down or up, the chart content changes but no `aria-live` announcement is made. Screen readers have no indication that the chart data has changed.

---

## 7. Keyboard

The chart container div is not focusable. Data points are not keyboard-navigable. The Back button is the only keyboard-accessible interactive element.

---

## 8. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DRILL-A1 | High | Chart area has no ARIA role or label — invisible to screen readers | Accepted-risk M1; add `role="img"` + dynamic `aria-label` reflecting current level in M2 |
| G-DRILL-A2 | High | No data table fallback at any drill level | Accepted-risk M1; implement visually-hidden table in M2 |
| G-DRILL-A3 | High | No `aria-live` announcement on level change — screen readers do not know the chart changed | Accepted-risk M1; add polite live region announcing new level in M2 |
| G-DRILL-A4 | Medium | Back button label "← Back" does not convey current drill depth or destination level | Accepted-risk M1; improve label to "Back to [level name]" in M2 |
| G-DRILL-A5 | Medium | Data points are not keyboard-navigable — drilldown is mouse-only | Accepted-risk M1; keyboard drilldown deferred to M3 |
