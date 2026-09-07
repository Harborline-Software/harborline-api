# SankeyChart — Interaction Contract

- **Component:** SankeyChart
- **ADR 0017 family:** DataVisualization
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SankeyChart.Semantic.md) · [Accessibility](./SankeyChart.Accessibility.md) · [Styling](./SankeyChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A23 SankeyChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; D3-Sankey / Recharts Sankey baseline)

---

## 1. Node hover

Hovering a node highlights all connected links (incoming + outgoing) at full opacity; all other links dim to `opacity-10`. Tooltip shows node name and total flow value.

---

## 2. Link hover

Hovering a link highlights it at full opacity; all other links dim. Tooltip shows `{source.name} → {target.name}: {value}`.

---

## 3. Node drag

Nodes are draggable vertically within their column to allow manual layout adjustment. Drag updates y position; layout is re-computed on release. Read-only mode (future `editable=false` prop) disables drag.

---

## 4. Animation

On mount: links animate with path-length stroke draw over 600ms. Node heights animate from 0 on data load.

> **Implementation note:** Path-draw animation via `stroke-dashoffset`/`stroke-dasharray` requires `SVGGeometryElement.getTotalLength()` which is only available after DOM mount (`useEffect`). This is NOT expressible as Tailwind classes. As a simpler alternative, use `opacity: 0 → 1` transition (`transition-opacity duration-600`) which achieves a similar fade-in effect with pure CSS.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SCHART1 | High | Node drag is pointer-only | Accepted-risk M1; manual layout is an enhancement; read-only is primary use case |
| G-SCHART2 | Medium | Dense graphs with many crossing links are difficult to read | Accepted-risk M1; layout algorithm is best-effort; upstream data simplification is consumer responsibility |
