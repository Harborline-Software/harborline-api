# ChartWizard — Accessibility Contract

- **Component:** ChartWizard
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ChartWizard.Semantic.md) · [Interaction](./ChartWizard.Interaction.md) · [Accessibility](./ChartWizard.Accessibility.md) · [Styling](./ChartWizard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartWizard.tsx` (PR 2676)
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

The outer `<div>` uses Tailwind `flex flex-col gap-2` and receives `className`. It has no ARIA role or label.

---

## 2. Toolbar buttons

The type-selector toolbar is a `<div role="group">` with the localized chart-type label. It contains one native HTML `<button>` per allowed type (inherently keyboard-focusable and activatable via Enter/Space). Button text is the human-readable type label (e.g., "Line", "Bar", "Area"). Each button exposes `aria-pressed`, which reflects whether that type is active.

---

## 3. Active state

The active chart type is communicated visually via color (`bg-primary` vs `bg-muted`) and programmatically via `aria-pressed="true"` on the active button; inactive buttons expose `aria-pressed="false"`. This behavior shipped in PR 2676.

---

## 4. Chart area

The `Chart` component rendered below the toolbar has the same accessibility gaps documented in `Chart.Accessibility.md` — no ARIA role, no data table fallback, no keyboard navigation.

---

## 5. Keyboard navigation

The toolbar buttons are natively focusable. Tab order moves through all visible type buttons in DOM order, then into the chart container. No arrow-key navigation within the toolbar group is implemented (standard radio-group / tab-list pattern is not used).

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-WIZ-A1 | Resolved | Active chart type is announced through button `aria-pressed` state | Resolved by PR 2676; toolbar uses `role="group"` and each button reflects the active type with `aria-pressed` |
| G-WIZ-A2 | High | Chart area has no ARIA role or label (inherits Chart.Accessibility gaps) | Accepted-risk M1; same remediation as Chart |
| G-WIZ-A3 | Medium | Toolbar has no group label — screen readers do not know the buttons select chart types | Accepted-risk M1; add `aria-label="Chart type"` on the toolbar div in M2 |
| G-WIZ-A4 | Medium | No `aria-live` announcement when chart type changes | Accepted-risk M1; add polite live region in M2 |
| G-WIZ-A5 | Low | No arrow-key navigation within the toolbar button group | Accepted-risk M1; implement roving tabindex pattern in M2 |
