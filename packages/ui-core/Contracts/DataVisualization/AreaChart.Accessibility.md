# AreaChart — Accessibility Contract

- **Component:** AreaChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./AreaChart.Semantic.md) · [Interaction](./AreaChart.Interaction.md) · [Styling](./AreaChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Recharts AreaChart baseline)
- **Catalog row:** #A1 AreaChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Recharts / Telerik baseline)

---

## 1. Root element

`<figure role="img" aria-label="{xAxisLabel} over time">`. Consumers must supply a meaningful `aria-label` describing the chart's content.

---

## 2. Data table fallback

A visually-hidden `<table>` inside the `<figure>` mirrors the chart data. Screen readers can navigate this table to access individual values.

---

## 3. SVG

The SVG element carries `aria-hidden="true"` — the data-table fallback is the AT-accessible path.

---

## 4. Tooltip

Hover tooltip is pointer-only and not keyboard-accessible. Screen readers use the data-table fallback.

---

## 5. Legend

Legend items are `<li>` elements with color swatches rendered as `<span aria-hidden="true">` (decorative). Series names are plain text.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AREA-A1 | Medium | Data-table fallback is forward-spec only — no implementation verified | Accepted-risk M1; apply WCAG 1.1.1 text alternative requirement at implementation time |
| G-AREA-A2 | Low | Color is the only differentiator between series — colorblind users rely on legend names | Accepted-risk M1; add pattern fills or marker shapes in M2 |
