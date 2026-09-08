# Sankey — Accessibility Contract

- **Component:** Sankey
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sankey.Semantic.md) · [Interaction](./Sankey.Interaction.md) · [Accessibility](./Sankey.Accessibility.md) · [Styling](./Sankey.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/Sankey.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root element

Plain `<div>` with no ARIA role or label. The Sankey diagram is semantically invisible to screen readers.

---

## 2. SVG / canvas

ECharts canvas/SVG has no accessibility attributes. Node names, link values, and flow structure are opaque to assistive technology.

---

## 3. Data table fallback

No data table fallback implemented. Nodes and links (including flow values) are inaccessible to AT.

---

## 4. Tooltip accessibility

ECharts item-trigger tooltip is pointer-driven and not keyboard-accessible.

---

## 5. Node drag

Node drag is a pointer-only interaction. Keyboard users cannot drag nodes to reposition the diagram.

---

## 6. Keyboard

Container and chart elements are not keyboard-focusable. No keyboard navigation.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SNKY-A1 | High | Root element has no ARIA role or label | Accepted-risk M1; add `role="img"` + `aria-label` at implementation hardening |
| G-SNKY-A2 | High | No data table fallback — node names and flow values inaccessible to AT | Accepted-risk M1; implement visually-hidden table (columns: source, target, value) in M2 |
| G-SNKY-A3 | Medium | Tooltip is pointer-only | Accepted-risk M1; deferred to M3 |
| G-SNKY-A4 | Low | Node drag is pointer-only | Accepted-risk M1; keyboard drag not planned; callers pre-arrange layout if order matters |
