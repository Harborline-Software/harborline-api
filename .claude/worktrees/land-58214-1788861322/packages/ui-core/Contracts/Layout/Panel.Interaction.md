# Panel — Interaction Contract

- **Component:** Panel
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Panel.Semantic.md) · [Accessibility](./Panel.Accessibility.md) · [Styling](./Panel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Panel.tsx`
- **Catalog row:** #95 Panel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `mode="accordion"` + `resizable`

---

## 1. Static mode — no interaction

`mode="static"` Panel is a pure display container. No event handlers, no state, no user interaction (unless `resizable` is set — see §3).

---

## 2. Accordion mode interaction

### 2.1 Toggle node click

Click on a toggle-node button (item with children):

1. If `item.disabled`: returns early (no state change, no callbacks).
2. Computes `willExpand = !expandedIds.has(id)`.
3. If `expandMode="single"`: clears all expanded IDs, then adds or removes the clicked ID.
4. If `expandMode="multiple"`: adds or removes the clicked ID only.
5. Calls `onExpand(item, willExpand)`.

### 2.2 Leaf node click

Click on a leaf-node button (item without children):

1. If `item.disabled`: returns early.
2. Calls `onSelect(item)`.

### 2.3 expandMode

- `"single"` (default): at most one item expanded at a time. Clicking an already-expanded item collapses it.
- `"multiple"`: any number of items can be expanded simultaneously.

---

## 3. Resizable mode interaction (static mode with `resizable` prop)

### 3.1 Pointer drag

`ResizeHandle` captures pointer on `pointerdown` via `setPointerCapture`. Tracks `pointermove` deltas from drag start:

- `'right'` handle: horizontal delta only → `dw` applied.
- `'bottom'` handle: vertical delta only → `dh` applied.
- `'corner'` handle: both deltas applied.

On `pointermove`: calls `onResizeBy(dw, dh)` → clamps to min/max → updates internal size state (uncontrolled) or calls `onResize(nextSize)` (both modes).

On `pointerup`: calls `onResizeEnd(currentSize)`.

### 3.2 Keyboard

Focused `ResizeHandle` responds to:

| Key | Direction | Step | Action |
|---|---|---|---|
| `ArrowRight` | right or corner | 10px (Shift: 50px) | increase width |
| `ArrowLeft` | right or corner | 10px (Shift: 50px) | decrease width |
| `ArrowDown` | bottom or corner | 10px (Shift: 50px) | increase height |
| `ArrowUp` | bottom or corner | 10px (Shift: 50px) | decrease height |
| `Enter` or `Space` | any | — | fires `onResizeEnd` |

Arrow keys call `preventDefault()` to prevent page scroll. Non-applicable arrow keys (e.g. ArrowDown on a right-only handle) are ignored.

### 3.3 Clamp

New size is clamped to `[minWidth, maxWidth]` × `[minHeight, maxHeight]` before state update and before `onResize` is called.

---

## 4. Known gaps

None.
