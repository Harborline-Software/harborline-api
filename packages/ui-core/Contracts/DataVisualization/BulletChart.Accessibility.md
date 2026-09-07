# BulletChart — Accessibility Contract

- **Component:** BulletChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./BulletChart.Semantic.md) · [Interaction](./BulletChart.Interaction.md) · [Styling](./BulletChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A11 BulletChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik / D3 baseline)

---

## 1. Root element

`<figure role="img" aria-label="{title}: {value} vs target {target}">`.

---

## 2. SVG

`aria-hidden="true"`. All meaningful data is in the `aria-label` and title/subtitle text nodes.

---

## 3. Range labels

Range band labels are rendered as SVG text or as a visually-hidden legend list. Not critical for AT since `aria-label` encodes the key result.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BULLET-A1 | Low | Range bands have color-only differentiation | Accepted-risk M1; range labels partially mitigate |
