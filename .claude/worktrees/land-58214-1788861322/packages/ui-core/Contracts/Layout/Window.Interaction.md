# Window — Interaction Contract

- **Component:** Window
- **ADR 0017 family:** Layout
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted (shipped behaviors) + Draft (expansion sections §FS-*)
- **Companion contracts:** [Semantic](./Window.Semantic.md) · [Styling](./Window.Styling.md) · [Accessibility](./Window.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Window.tsx`
- **Catalog row:** #149 Window (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

This contract describes **how Window behaves in response to user input** — the drag
and resize mechanics, stage transitions, and the boundary between controlled and
uncontrolled operation. Prop shapes are in the [Semantic contract](./Window.Semantic.md);
visual styling and ARIA are in the Styling and Accessibility contracts.

---

## 2. Drag (Accepted)

Window uses pointer events (`onPointerDown` / `onPointerMove` / `onPointerUp`) for
all input, with `setPointerCapture` to lock tracking to the initiating element.

### 2.1 Trigger

| # | Behavior |
|---|---|
| D-1 | Drag initiates on `onPointerDown` on the **title bar** — the `<div>` containing the title text and control buttons. |
| D-2 | Drag is **blocked** when `draggable === false`. |
| D-3 | Drag is **blocked** when `state !== 'default'` (minimized or maximized windows cannot be dragged). |
| D-4 | Pointer events on the control-buttons container (`div.flex.items-center.gap-1`) stop propagation before reaching the title bar's drag handler — clicking Minimize/Maximize/Close does NOT initiate drag. |

### 2.2 In-flight

| # | Behavior |
|---|---|
| D-5 | On drag start, the delta origin is captured: `{ startX, startY, origTop, origLeft }`. |
| D-6 | On each `onPointerMove` while drag is active, position updates continuously: `top = origTop + (clientY − startY)`, `left = origLeft + (clientX − startX)`. |
| D-7 | Position is stored in component state (`useState`) and applied as inline `style.top` / `style.left` on the window container. |
| D-8 | No bounds clamping is applied in M1 — the window can be dragged anywhere including off-screen. (See §FS-1.4 for wave-2 bounds.) |

### 2.3 Drag end

| # | Behavior |
|---|---|
| D-9 | Drag terminates on `onPointerUp`. The `dragRef` is cleared (`null`). |
| D-10 | The final position persists in component state until the next drag or until the component unmounts. |

---

## 3. Resize (Accepted)

### 3.1 Handles

Three resize zones are rendered when `resizable === true` and `state === 'default'`:

| Handle | CSS cursor | Edge |
|---|---|---|
| SE corner | `cursor-se-resize` | Expands width (east) + height (south) simultaneously |
| S edge | `cursor-s-resize` | Expands height (south) only |
| E edge | `cursor-e-resize` | Expands width (east) only |

### 3.2 Trigger

| # | Behavior |
|---|---|
| R-1 | Resize initiates on `onPointerDown` on a resize handle. `stopPropagation` prevents the drag handler from also triggering. |
| R-2 | Resize is **blocked** when `resizable === false`. |
| R-3 | Resize handles are **absent** (not rendered) when `state !== 'default'`. |

### 3.3 In-flight

| # | Behavior |
|---|---|
| R-4 | On resize start, the delta origin is captured: `{ edge, startX, startY, origW, origH }`. |
| R-5 | On each `onPointerMove` while resize is active, dimensions update continuously. Width and height are computed independently from the delta and the captured origin. |
| R-6 | `width = Math.max(minWidth, origW + dx)` for E-direction edges; `height = Math.max(minHeight, origH + dy)` for S-direction edges. Minimum constraints are enforced every update. |
| R-7 | The resize handler is invoked by the same `onPointerMove` listener as the drag handler; only one can be active at a time (whichever started first). |

### 3.4 Resize end

| # | Behavior |
|---|---|
| R-8 | Resize terminates on `onPointerUp`. The `resizeRef` is cleared. |
| R-9 | The final size persists in component state. |

---

## 4. Stage transitions (Accepted)

Window supports three stages: `'default'`, `'minimized'`, `'maximized'`.

### 4.1 Minimize / Restore

| # | Behavior |
|---|---|
| S-1 | Clicking Minimize from `'default'` fires `onStateChange('minimized')` and (in uncontrolled mode) transitions internal state to `'minimized'`. |
| S-2 | Clicking Minimize from `'minimized'` (Restore) fires `onStateChange('default')` and transitions to `'default'`. |
| S-3 | When `state === 'minimized'`, the body (`children`) is **not rendered** at all. Only the title bar is visible. |
| S-4 | When `state === 'minimized'` and `modal === true`, the modal backdrop is **not rendered**. |
| S-5 | Resize handles are absent when `state === 'minimized'`. |

### 4.2 Maximize / Restore

| # | Behavior |
|---|---|
| S-6 | Clicking Maximize from `'default'` fires `onStateChange('maximized')` and transitions to `'maximized'`. |
| S-7 | Clicking Maximize from `'maximized'` (Restore) fires `onStateChange('default')` and transitions to `'default'`. |
| S-8 | When `state === 'maximized'`, the window covers the full viewport via `position: fixed; inset: 0; width: 100vw; height: 100vh`. |
| S-9 | Resize handles are absent when `state === 'maximized'`. |
| S-10 | Drag is blocked when `state === 'maximized'`. |

### 4.3 Controlled state

| # | Behavior |
|---|---|
| S-11 | When `state` prop is supplied, the component is **controlled**. Internal `windowState` changes (from button clicks) fire `onStateChange` but do NOT visibly change the window until the host updates the `state` prop. |
| S-12 | The effective state is computed as `controlledState ?? windowState` — controlled takes precedence. |

---

## 5. Close (Accepted)

| # | Behavior |
|---|---|
| C-1 | Clicking Close fires `onClose()`. |
| C-2 | Window does NOT unmount itself. The host is responsible for removing `<Window>` from the tree in response to `onClose`. |
| C-3 | When `closable === false`, the Close button is absent and `onClose` is never fired via button click. |

---

## 6. Pointer event architecture

The `onPointerMove` and `onPointerUp` handlers are attached to the **window
container div**, not the document. This means:

- Moving the pointer outside the window container stops drag/resize updates
  until the pointer re-enters (unless `setPointerCapture` has been set).
- `setPointerCapture` is called on drag and resize start, which locks move
  events to the capturing element even when the pointer leaves the element bounds.
  This ensures smooth drag/resize without losing tracking at element edges.

---

## 7. Deferred behaviors (M1)

- `onMove` position callback during drag — §FS-1.2
- `onResize` size callback during resize — §FS-1.3
- Drag bounds clamping — §FS-1.4
- Double-click title bar to maximize — §FS-2.1
- Keyboard-driven move (Arrow keys on focused title bar) — §FS-3.1
- Keyboard-driven resize — §FS-3.1
- Escape key to close (or restore from maximized) — §FS-3.2

---

## Full-surface expansion (Draft — waves 2-3, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

---

### §FS-1 Wave-2 — move/resize callbacks + bounds

#### §FS-1.2 `onMove` — drag position callback

See Semantic §FS-1.2 for the prop declaration.

**Behavioral rules:**
1. `onMove` fires on every `onPointerMove` while `dragRef.current !== null`.
2. Payload is the **already-applied** new position: `{ top: number; left: number }`.
3. When `dragBounds` (§FS-1.4) is active, the payload reflects the **clamped**
   position, not the raw pointer-delta position.
4. `onMove` does NOT fire during keyboard-driven move (that fires a separate
   `onKeyboardMove` callback — §FS-3.1).

**wave-2**

---

#### §FS-1.3 `onResize` — resize callback

See Semantic §FS-1.3 for the prop declaration.

**Behavioral rules:**
1. `onResize` fires on every `onPointerMove` while `resizeRef.current !== null`.
2. Payload is the **already-clamped** new size: `{ width: number; height: number }`.
3. Does NOT fire when the resize is clamped and size is unchanged (e.g., the
   pointer moved left past the `minWidth` boundary — the size stays at `minWidth`
   and `onResize` is skipped to avoid noise).

**wave-2**

---

#### §FS-1.4 Drag bounds clamping

See Semantic §FS-1.4 for the prop declaration.

**Behavioral rules:**
1. On each `onPointerMove` during drag, compute the unclamped new position, then
   apply bounds:
   - `top = clamp(top, bounds.top ?? -Infinity, (bounds.bottom ?? Infinity) − windowHeight)`
   - `left = clamp(left, bounds.left ?? -Infinity, (bounds.right ?? Infinity) − windowWidth)`
2. `'viewport'` expands to `{ top: 0, left: 0, right: window.innerWidth, bottom: window.innerHeight }`
   evaluated at each move event.
3. The window height for clamping is the **current** rendered height (`size.height` for
   default state; title-bar height for minimized state). Maximized state cannot be dragged
   (D-3), so bounds don't apply.

**wave-2**

---

### §FS-2 Wave-3 — stage UX

#### §FS-2.1 Double-click to maximize

See Semantic §FS-2.1 for the prop declaration (`doubleClickStageChange`).

**Behavioral rules:**
1. `onDoubleClick` handler on the title bar triggers the stage toggle.
2. The handler is guarded: it fires only on the title bar div itself, not on
   descendant control buttons (which stop propagation from single-click handlers
   but not from double-click — the `onDoubleClick` guard checks
   `e.target === titleBarRef.current` or `e.currentTarget === titleBarRef.current`).
3. From `'default'` → fires `onStateChange('maximized')`.
4. From `'maximized'` → fires `onStateChange('default')`.
5. No effect when `state === 'minimized'`.
6. When `doubleClickStageChange === false`, the `onDoubleClick` handler is not
   attached (not just ignored).

**wave-3**

---

### §FS-3 Wave-3 — keyboard

#### §FS-3.1 Keyboard move and resize (WAI-ARIA dialog/window pattern)

Per WAI-ARIA Authoring Practices Guide "Window Splitter" and "Dialog" patterns,
a moveable window SHOULD support keyboard-driven position control.

**Keyboard move (when title bar is focused):**

| Key | Behavior |
|---|---|
| `Arrow Up` | Move window up by 10px |
| `Arrow Down` | Move window down by 10px |
| `Arrow Left` | Move window left by 10px |
| `Arrow Right` | Move window right by 10px |
| `Shift + Arrow` | Move window by 50px (large step) |

**Behavioral rules:**
1. Arrow keys only move the window when `draggable === true` and `state === 'default'`.
2. The title bar element MUST have `tabIndex={0}` to be focusable.
3. Movement is blocked at viewport edges when `dragBounds === 'viewport'`.
4. `onMove` fires on each keyboard-driven position update (same contract as pointer
   drag — §FS-1.2).

**Keyboard resize (when a resize handle is focused):**

| Key | Behavior |
|---|---|
| `Arrow Up` | Decrease height by 10px (S-resize handle focused) |
| `Arrow Down` | Increase height by 10px |
| `Arrow Left` | Decrease width by 10px (E-resize handle focused) |
| `Arrow Right` | Increase width by 10px |
| `Shift + Arrow` | 50px step |

**Behavioral rules:**
1. Resize handles MUST have `tabIndex={0}` and `role="separator"` with
   `aria-label` per the Accessibility contract §FS-3.
2. Size changes are clamped to `minWidth` / `minHeight`.
3. `onResize` fires on each keyboard-driven size update.

**wave-3**

---

#### §FS-3.2 Escape key handling

| # | Behavior |
|---|---|
| E-1 | When the window (or any focusable descendant) has focus and the user presses Escape, fire `onClose()` — same as clicking Close. |
| E-2 | Escape does NOT close the window when `closable === false`. |
| E-3 | When `state === 'maximized'` and Escape is pressed, restore to `'default'` (fire `onStateChange('default')`) rather than closing — matching the Kendo convention that Escape restores fullscreen before close. A second Escape then closes. |

**wave-3**
