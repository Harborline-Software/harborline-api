# Diagram — Semantic Contract (stub)

- **Component:** Diagram
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted (stub — out-of-scope for @harborline-software/ui-react v1+)
- **Companion contracts:** none (out-of-scope stub)
- **Reference implementation:** none
- **Catalog row:** #U4 Diagram (`app-priority: low`, `library-scope: out-of-scope`)
- **Phase:** ADR 0017-A1 out-of-scope stub

---

Diagram is a Telerik/KendoReact canvas-based component for rendering flow diagrams, mind maps, and network graphs with draggable nodes and editable connections. It is out of scope for `@harborline-software/ui-react` because its use cases within the Harborline application are covered by more specialized tools: organizational hierarchies use OrgChart; flow diagrams use SankeyChart; process maps and dependency graphs are rare enough to warrant a direct integration of React Flow or a headless SVG approach at the application layer. The Diagram component's breadth and canvas dependency create a disproportionate bundle-size cost for its expected usage frequency.

**NOTE — partial de-scope 2026-06-11:** a hand-rolled SVG `Diagram.tsx` implementation
exists at `packages/ui-react/src/components/datagrid/Diagram.tsx` and is covered by
the kendo-spec-audit at ~40%. The interaction expansion below (§FS) specs the
interactive surface for this existing SVG implementation — it does NOT spec the
full Kendo canvas-based Diagram. Bundle-size concerns remain; the sections below
expand the shipped SVG-based component only.

---

## Full-surface expansion — shipped SVG Diagram (2026-06-11 — waves 2-4, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

**Reference implementation:** `packages/ui-react/src/components/datagrid/Diagram.tsx`
(SVG-based; hand-rolled; does NOT use Kendo canvas)

---

### §FS-1 Wave-2 — drag-to-move nodes

**Status:** Draft

#### §FS-1.1 Drag mechanics

Nodes are draggable via pointer when `editable === true` (new prop; default `false`).
Dragging a node moves it in the SVG viewport:

1. `pointerdown` on a node sets the active drag node and records the pointer offset
   from the node's top-left corner.
2. `pointermove` on the SVG surface (captured via `setPointerCapture`) updates the
   node's position in a local draft-positions map. The node and all connected edges
   re-render at the new position on each `pointermove` (no lag / snap).
3. `pointerup` commits the new position: fires
   `onNodePositionChange(nodeId, { x, y })` with the final top-left coordinates.
4. If `onNodePositionChange` is not supplied, the position is stored in internal state
   (uncontrolled drag). If supplied, the host controls the position (controlled drag).

`x` and `y` in `onNodePositionChange` are in SVG user units (the same coordinate
space as `DiagramNode.x` / `DiagramNode.y`).

Nodes cannot be dragged outside the SVG bounds (clamp at `0` on all sides and at
`svgWidth - nodeWidth` / `svgHeight - nodeHeight` on the far sides). When
`fitContent === true`, the SVG bounds expand to fit the furthest node after each drag.

```typescript
// Addition to DiagramProps:
editable?: boolean
onNodePositionChange?: (nodeId: string, position: { x: number; y: number }) => void
```

#### §FS-1.2 Edge follow

All edges connected to the dragged node continuously update during the drag
(the bezier control points recalculate on each `pointermove`). Connected edges are
identified from the `edges` prop by matching `edge.from === nodeId || edge.to === nodeId`.

#### §FS-1.3 Keyboard reposition (wave-3 dependency)

Pointer-only drag in wave-2. Keyboard repositioning (arrow-key nudge on focused nodes)
is deferred to wave-3 as part of the selection + keyboard-navigation pass.

**wave-2**

---

### §FS-2 Wave-2 — pan and zoom

**Status:** Draft

#### §FS-2.1 Pan

```typescript
// Addition to DiagramProps:
pannable?: boolean      // default: false
defaultPan?: { x: number; y: number }   // uncontrolled initial offset
pan?: { x: number; y: number }           // controlled
onPanChange?: (pan: { x: number; y: number }) => void
```

When `pannable === true` and the user is NOT hovering over a node or edge:
- `pointerdown` + `pointermove` on the SVG background pans the viewport by translating
  the inner `<g>` element via a CSS transform `translate(x, y)`.
- `pointerup` commits the new pan offset. Fires `onPanChange(newPan)`.
- Cursor changes to `grab` on hover over the SVG background, `grabbing` while dragging.

Two-finger trackpad scroll also pans (via `wheel` event without Ctrl key).

#### §FS-2.2 Zoom

```typescript
// Addition to DiagramProps:
zoomable?: boolean      // default: false
defaultZoom?: number    // default: 1.0
zoom?: number           // controlled
onZoomChange?: (zoom: number) => void
minZoom?: number        // default: 0.25
maxZoom?: number        // default: 3.0
```

Zoom is applied as a CSS `scale(zoom)` on the inner `<g>` in combination with the pan
transform: `transform: translate(panX, panY) scale(zoom)`.

Zoom triggers:
- `Ctrl+wheel` (mouse) or pinch gesture (touch / trackpad) → incremental zoom by 0.1
  per step, clamped to `[minZoom, maxZoom]`. Fires `onZoomChange`.
- Programmatic: host sets `zoom` prop.

Zoom origin: the pointer position at the moment of the wheel event (zoom-to-cursor
behavior). When zoom is programmatic (prop change), zoom origin is the center of the
SVG viewport.

A reset button (optional; rendered when `showZoomControls === true`) resets zoom to 1.0
and pan to `{ x: 0, y: 0 }`.

**wave-2**

---

### §FS-3 Wave-3 — node and edge selection

**Status:** Draft

#### §FS-3.1 Selection model

```typescript
// Addition to DiagramProps:
selectable?: boolean     // default: false
selectedNodes?: string[]   // controlled; node ids
selectedEdges?: string[]   // controlled; edge ids
defaultSelectedNodes?: string[]
defaultSelectedEdges?: string[]
onSelectionChange?: (nodes: string[], edges: string[]) => void
```

When `selectable === true`:
- Clicking a node (or edge) adds it to the selection; clicking again removes it.
- Clicking the SVG background clears the selection.
- `Ctrl+click` (Mac: `Cmd+click`) toggles the clicked item without clearing others
  (multi-select).
- Shift+drag on the SVG background draws a rectangular selection marquee and selects
  all nodes whose bounding boxes intersect the marquee.

Selected nodes render with an elevated border/stroke (design token: `border-selected`).
Selected edges render with an elevated stroke color.

`onSelectionChange` fires after every selection change with the complete new selection arrays.

#### §FS-3.2 Multi-select delete

When `editable === true` and `selectable === true`, pressing `Delete` or `Backspace`
with a non-empty selection fires:
- `onNodesDelete(selectedNodeIds)` — new prop.
- `onEdgesDelete(selectedEdgeIds)` — new prop.

```typescript
onNodesDelete?: (nodeIds: string[]) => void
onEdgesDelete?: (edgeIds: string[]) => void
```

If the host uses controlled nodes/edges props, it must remove the deleted items and
re-pass updated props. If uncontrolled, the component removes them from internal state.

**wave-3**

---

### §FS-4 Wave-3 — interactive connection drawing

**Status:** Draft

#### §FS-4.1 Connection draw gesture

When `editable === true`, nodes render a small connection handle at their border
on hover (a circle anchor, 8px diameter, centered on each cardinal edge midpoint).
Hovering over the node reveals all four anchors; the cursor changes to `crosshair`.

Drag gesture:
1. `pointerdown` on an anchor starts a "drawing" edge. A ghost edge path follows the
   cursor from the source anchor.
2. The ghost edge snaps to the nearest anchor on any node the cursor enters.
3. `pointerup` on a node anchor fires
   `onEdgeCreate({ from: sourceNodeId, to: targetNodeId })`.
4. `pointerup` on the SVG background (no target) cancels the drawing gesture —
   no callback fires.

```typescript
// Addition to DiagramProps:
onEdgeCreate?: (edge: { from: string; to: string }) => void
```

The host is responsible for appending the new edge to `edges` (controlled pattern).

Self-connections (from === to) are disallowed — dragging back to the source node
shows a `not-allowed` cursor on the source node's anchors.

Duplicate edges (same from + to already exists) are allowed by default; the host can
reject via `onEdgeCreate` if policy requires uniqueness.

**wave-3**

---

### §FS-5 Wave-4 — layout algorithms

**Status:** Draft

#### §FS-5.1 Layout prop

```typescript
type DiagramLayoutAlgorithm = 'grid' | 'tree' | 'force' | 'layered'

// Addition to DiagramProps:
layout?: DiagramLayoutAlgorithm    // default: 'grid' (current behavior)
layoutOptions?: DiagramLayoutOptions
```

```typescript
interface DiagramLayoutOptions {
  direction?: 'top-down' | 'left-right' | 'bottom-up' | 'right-left'  // tree + layered
  rankSpacing?: number     // vertical spacing between ranks (tree/layered)
  nodeSpacing?: number     // horizontal spacing between nodes in the same rank
  iterations?: number      // force layout convergence iterations (default: 100)
}
```

#### §FS-5.2 Algorithm contracts

**`'grid'`** (current behavior): nodes arranged in AUTO_COLS-wide row-major grid.
Explicit `node.x` / `node.y` override grid placement.

**`'tree'`**: hierarchical Sugiyama-style layout. Requires edges to form a DAG (or near-
DAG; cycles are broken by removing the back-edge with the lowest in-degree). Root nodes
(nodes with no incoming edges) are placed at the top rank (or left for `left-right`).
Child nodes are centered under their parent. Node.x / node.y overrides are ignored.

**`'force'`**: force-directed spring layout. Nodes repel each other (configurable
repulsion constant); edges pull connected nodes together (spring stiffness). Runs for
`iterations` steps from an initial grid seed. Result is non-deterministic across runs;
hosts requiring stable output should cache the positions and pass them back as
`node.x` / `node.y` (which override the layout when `layout !== 'grid'`).

**`'layered'`**: assign nodes to horizontal layers by topological depth (Kahn's
algorithm). Nodes in the same layer are evenly spaced. Back-edges are drawn curved
around the layers. Same direction options as `'tree'`.

**NOTE:** Layout algorithms run synchronously during render. For graphs >500 nodes,
the host should compute layout offline and pass explicit `node.x` / `node.y`.

**wave-4**
