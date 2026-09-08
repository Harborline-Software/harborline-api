# RadialGauge — Accessibility Contract

- **Component:** RadialGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RadialGauge.Semantic.md) · [Interaction](./RadialGauge.Interaction.md) · [Styling](./RadialGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/gauges/RadialGauge.tsx` (PR 2677)
- **Catalog row:** #A21 RadialGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik RadialGauge baseline)
- **Aliases-canonical-ref:** [CircularGauge](./CircularGauge.Accessibility.md) _(link only; update manually when canonical changes)_

---

## 1. Accessibility

Extends CircularGauge — see `CircularGauge.Accessibility.md`.

`role="meter"` + `aria-valuenow/min/max` on root figure. SVG `aria-hidden="true"`.

`aria-valuetext="{value} {unit}"` MUST be specified to provide a formatted value with unit to AT (e.g., "72 km/h" rather than the raw integer "72"). Also render a visually-hidden `<span>` with the formatted value as a belt-and-suspenders fallback for Safari/VoiceOver, which does not fully support `role="meter"` on `<div>` or `<figure>` elements.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RGAUGE-A1 | Low | Range band colors have no text equivalent for colorblind users | Accepted-risk M1; `aria-label` provides the numeric value regardless of color |
| G-RGAUGE-A2 | Medium | Safari/VoiceOver does not reliably support `role="meter"` on `<div>`/`<figure>` | Accepted-risk M1; `aria-valuetext` + visually-hidden span fallback mitigates |
