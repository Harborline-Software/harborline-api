# OrgChart — Semantic Contract

- **Component:** OrgChart
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./OrgChart.Interaction.md) · [Accessibility](./OrgChart.Accessibility.md) · [Styling](./OrgChart.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A24 OrgChart (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik OrgChart baseline)

---

## 1. Component purpose

**OrgChart** — a hierarchical tree chart for visualizing organizational structure. Nodes are arranged in a top-down tree with connector lines. Supports node collapse/expand, custom node templates, and a pan/zoom canvas.

---

## 2. Data model

```typescript
interface OrgNode {
  id: string | number
  title: string
  subtitle?: string
  avatarUrl?: string
  children?: OrgNode[]
  expanded?: boolean
  [key: string]: unknown
}

interface OrgChartProps {
  data: OrgNode
  nodeRender?: (node: OrgNode) => React.ReactNode
  onNodeClick?: (node: OrgNode) => void
  onNodeExpand?: (node: OrgNode, expanded: boolean) => void
  pannable?: boolean
  zoomable?: boolean
  defaultZoom?: number
  className?: string
}
```

---

## 3. Tree layout

Nodes are laid out top-down. Siblings are evenly spaced horizontally. Subtree collapse hides all descendants and repositions siblings. `data` is the root node.

---

## 4. Node template

Default node renders: avatar (if present) + title + subtitle. `nodeRender` overrides the entire node content. Node width is fixed; height is auto from content.

---

## 5. Expand/collapse

Nodes with children render a toggle button (chevron). On toggle, `onNodeExpand` fires. State is internal unless the consumer sets `node.expanded` explicitly (controlled mode).
