# ArcGauge — Accessibility Contract

- **Component:** ArcGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ArcGauge.Semantic.md) · [Interaction](./ArcGauge.Interaction.md) · [Styling](./ArcGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/gauges/ArcGauge.tsx` (PR 2677)
- **Catalog row:** #A20 ArcGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ArcGauge baseline)

---

## 1. Root element

`<div role="meter" aria-valuenow="{value}" aria-valuemin="{min}" aria-valuemax="{max}" aria-label="Arc gauge: {value}">`.

`aria-valuetext="{value} {unit}"` MUST be specified to provide a formatted value with unit to AT (e.g., "72 km/h" rather than the raw integer "72"). Also render a visually-hidden `<span>` with the formatted value as a belt-and-suspenders fallback for Safari/VoiceOver, which does not fully support `role="meter"` on `<div>` or `<figure>` elements.

---

## 2. SVG

`aria-hidden="true"`.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ARCGAUGE-A2 | Medium | Safari/VoiceOver does not reliably support `role="meter"` on `<div>`/`<figure>` | Accepted-risk M1; `aria-valuetext` + visually-hidden span fallback mitigates |
