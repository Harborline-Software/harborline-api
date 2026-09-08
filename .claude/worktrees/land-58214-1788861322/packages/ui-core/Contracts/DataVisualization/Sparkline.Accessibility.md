# Sparkline — Accessibility Contract

- **Component:** Sparkline
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sparkline.Semantic.md) · [Interaction](./Sparkline.Interaction.md) · [Styling](./Sparkline.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik Sparkline baseline)
- **Catalog row:** #120 Sparkline (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Sparkline baseline)

---

## 1. Root element

`<svg role="img" aria-label="{aria-label prop}">`. The `aria-label` prop is required; consumers must supply a meaningful description (e.g. "Revenue trend: 12, 14, 11, 18, 20").

---

## 2. Title element

`<title>` inside the SVG mirrors the `aria-label` value. Provides a screen-reader-accessible label in browsers that surface SVG titles.

---

## 3. Tooltip

When `tooltip=true`: tooltip is `role="tooltip"` with `id`. The tooltip is pointer-only. AT users rely on the `aria-label` on the `<figure>` element (§1). Do NOT wire `aria-describedby` to the tooltip element — the tooltip is never available for keyboard or AT users, making the reference misleading or broken.

---

## 4. Keyboard navigation

Sparkline is not keyboard-navigable. It is a data visualization aid, not an interactive control. `tabIndex` is not set on the root SVG.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SPK-A1 | Medium | Data values are not individually accessible via AT — screen reader reads only the aggregate `aria-label` | Accepted-risk M1; `aria-label` summary mitigates for AT users; per-point AT navigation deferred to M3 |
| G-SPK-A2 | Medium | No data-table fallback for non-visual users | Accepted-risk M1; consumers can render a visually-hidden `<table>` alongside the Sparkline if needed |
