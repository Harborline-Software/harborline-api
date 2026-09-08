# LinearGauge — Accessibility Contract

- **Component:** LinearGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./LinearGauge.Semantic.md) · [Interaction](./LinearGauge.Interaction.md) · [Styling](./LinearGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/gauges/LinearGauge.tsx` (PR 2677)
- **Catalog row:** #A19 LinearGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik LinearGauge baseline)

---

## 1. Root element

`<div role="meter" aria-valuenow="{value}" aria-valuemin="{min}" aria-valuemax="{max}" aria-label="Gauge: {value}">`.

`aria-valuetext="{value} {unit}"` MUST be specified to provide a formatted value with unit to AT (e.g., "72 km/h" rather than the raw integer "72"). Also render a visually-hidden `<span>` with the formatted value as a belt-and-suspenders fallback for Safari/VoiceOver, which does not fully support `role="meter"` on `<div>` or `<figure>` elements.

---

## 2. Value label

Visible `{value}` text node adjacent to the bar. Combined with `aria-valuenow` for AT.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-LGAUGE-A1 | Low | Inherits G-CGAUGE-A1: Range band colors have no text equivalent for colorblind users | Accepted-risk M1; `aria-label` provides the numeric value regardless of color |
| G-LGAUGE-A2 | Medium | Safari/VoiceOver does not reliably support `role="meter"` on `<div>`/`<figure>` | Accepted-risk M1; `aria-valuetext` + visually-hidden span fallback mitigates |
