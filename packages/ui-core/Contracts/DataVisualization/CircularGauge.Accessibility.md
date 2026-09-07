# CircularGauge — Accessibility Contract

- **Component:** CircularGauge
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CircularGauge.Semantic.md) · [Interaction](./CircularGauge.Interaction.md) · [Styling](./CircularGauge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/gauges/CircularGauge.tsx` (PR 2677)
- **Catalog row:** #A18 CircularGauge (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik CircularGauge baseline)

---

## 1. Root element

`<figure role="meter" aria-valuenow="{value}" aria-valuemin="{min}" aria-valuemax="{max}" aria-label="{centerLabel text or consumer-supplied}">`

`aria-valuetext="{value} {unit}"` MUST be specified to provide a formatted value with unit to AT (e.g., "72 km/h" rather than the raw integer "72"). Also render a visually-hidden `<span>` with the formatted value as a belt-and-suspenders fallback for Safari/VoiceOver, which does not fully support `role="meter"` on `<div>` or `<figure>` elements.

---

## 2. Value text

`centerLabel` renders a visible numeric value. For AT, `aria-valuenow` is the primary signal.

---

## 3. SVG

`aria-hidden="true"` — the `role="meter"` on the root is the AT-accessible path.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CGAUGE-A1 | Low | Range band colors have no text equivalent for colorblind users | Accepted-risk M1; `aria-label` provides the numeric value regardless of color |
| G-CGAUGE-A2 | Medium | Safari/VoiceOver does not reliably support `role="meter"` on `<div>`/`<figure>` | Accepted-risk M1; `aria-valuetext` + visually-hidden span fallback mitigates |
