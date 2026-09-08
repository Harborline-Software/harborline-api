# SankeyChart — Accessibility Contract

- **Component:** SankeyChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SankeyChart.Semantic.md) · [Interaction](./SankeyChart.Interaction.md) · [Styling](./SankeyChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A23 SankeyChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; D3-Sankey / Recharts Sankey baseline)

---

## 1. Structure

`<figure role="img" aria-label="Sankey flow diagram">` wrapping SVG (`aria-hidden="true"`) + visually-hidden structured list fallback.

---

## 2. Data fallback

The visually-hidden fallback is a `<ul>` of nodes, each with a nested `<ul>` of outgoing links: `"{source.name} to {target.name}: {value}"`. This provides a complete textual traversal of the flow graph for screen reader users.

---

## 3. Color

Links use source-node colors. Multiple overlapping links between the same pair should differ in opacity or pattern — pure hue differentiation fails WCAG 1.4.1 when flows are stacked.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SCHART-A1 | High | Complex Sankey diagrams with many nodes are inherently spatial and not equivalent via text summary | Accepted-risk M1; textual list fallback gives data completeness even if spatial layout is lost |
| G-SCHART-A2 | Medium | Link hover interaction is pointer-only | Accepted-risk M1; read-only charts; AT uses data fallback |
