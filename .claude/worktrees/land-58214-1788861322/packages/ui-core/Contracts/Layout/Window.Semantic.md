# Window — Semantic Contract

- **Component:** Window
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted (shipped surface) + Draft (expansion sections §FS-*)
- **Companion contracts:** [Interaction](./Window.Interaction.md) · [Styling](./Window.Styling.md) · [Accessibility](./Window.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Window.tsx`
- **Catalog row:** #149 Window (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (reverse-spec from shipping implementation) + Draft expansion

---

## 1. Purpose

Window is a floating, draggable, resizable overlay panel. Unlike Dialog (which is
purely modal and blocking), Window supports minimize/maximize state transitions,
free positioning, and coexisting non-modal instances. It is the design system's
"desktop windowing" primitive — useful for live data panels, floating forms,
pinned utilities, and detail viewers that the user repositions during a session.

This contract is a **reverse-spec** of the shipping implementation (15 tests;
`packages/ui-react/src/components/layout/Window.tsx`). The Accepted sections
document exactly what ships. The Draft expansion sections (§FS-*) cover the
Kendo minimum surface not yet shipped, tagged with their target wave.

---

## 2. Props — shipped surface (Accepted)

```typescript
export type WindowState = 'default' | 'minimized' | 'maximized'

export interface WindowProps {
  // Content
  title?: React.ReactNode
  children?: React.ReactNode

  // Initial size (also the controlled size when uncontrolled)
  width?: number | string     // default 400
  height?: number | string    // default 300

  // Initial position (applied once on mount; not reactive to prop changes)
  top?: number                // default 80
  left?: number               // default 80

  // Constraints
  minWidth?: number           // default 200; resize cannot go below this
  minHeight?: number          // default 100; resize cannot go below this

  // Feature flags
  draggable?: boolean         // default true
  resizable?: boolean         // default true
  closable?: boolean          // default true  — shows/hides Close button
  minimizable?: boolean       // default true  — shows/hides Minimize button
  maximizable?: boolean       // default true  — shows/hides Maximize button
  modal?: boolean             // default false — renders backdrop overlay

  // Controlled / uncontrolled state
  state?: WindowState         // controlled; overrides internal state when supplied
  defaultState?: WindowState  // uncontrolled seed; default 'default'

  // Callbacks
  onClose?: () => void
  onStateChange?: (state: WindowState) => void

  // HTML
  className?: string          // applied to the root window container div
}
```

---

## 3. Props table — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `title` | `React.ReactNode` | — | Title bar content. Rendered in a `<span>` with `truncate`; accepts string or any ReactNode. |
| `children` | `React.ReactNode` | — | Body content. Suppressed (not rendered) when `state === 'minimized'`. |
| `width` | `number \| string` | `400` | Initial window width in pixels (number) or any CSS length string. Parsed to integer internally; non-parseable falls back to 400. |
| `height` | `number \| string` | `300` | Initial window height. Same parsing rule as `width`. |
| `top` | `number` | `80` | Initial top position (`position: fixed`). Applied on mount; not reactive to prop changes after mount. |
| `left` | `number` | `80` | Initial left position. Same rules as `top`. |
| `minWidth` | `number` | `200` | Minimum width enforced during resize via `Math.max`. |
| `minHeight` | `number` | `100` | Minimum height enforced during resize. |
| `draggable` | `boolean` | `true` | Enables pointer-event drag on the title bar. Drag is suppressed when `state !== 'default'`. |
| `resizable` | `boolean` | `true` | Renders SE/S/E resize handles. Handles are absent when `state !== 'default'` or `state === 'minimized'`. |
| `closable` | `boolean` | `true` | Renders the Close (×) button. When `false`, the button is absent entirely. |
| `minimizable` | `boolean` | `true` | Renders the Minimize (—) button. |
| `maximizable` | `boolean` | `true` | Renders the Maximize (□/❐) button. |
| `modal` | `boolean` | `false` | When `true`, renders a `position: fixed; inset: 0` backdrop behind the window. Backdrop is suppressed when `state === 'minimized'`. |
| `state` | `WindowState` | omitted | Controlled stage. When supplied, the component reflects this state and internal state changes do not persist across renders — the host must handle `onStateChange` to update the controlled value. |
| `defaultState` | `WindowState` | `'default'` | Uncontrolled initial stage. Ignored if `state` is supplied. |
| `onClose` | `() => void` | — | Fired when the Close button is clicked. Window does NOT remove itself from the DOM — the host must unmount it in response. |
| `onStateChange` | `(state: WindowState) => void` | — | Fired on every stage transition (minimize/restore/maximize/restore). Payload is the **next** stage string. |
| `className` | `string` | — | Extra Tailwind / CSS classes merged onto the root window `<div>`. |

### 3.1 Controlled vs. uncontrolled state

Window supports the standard React duality:

- **Uncontrolled (default):** omit `state`. The component manages stage internally
  (`useState` seeded from `defaultState`). `onStateChange` fires for observation
  but the host does not need to feed state back.
- **Controlled:** supply `state`. The component mirrors the host's value. The host
  MUST handle `onStateChange` and update `state` accordingly, or button clicks will
  appear to have no effect (the controlled value wins over internal state).

### 3.2 Position and size reactivity

`top`, `left`, `width`, `height` are **mount-time-only props**. They seed the
internal `pos` and `size` state on first render. Subsequent prop changes do NOT
move or resize the window — the user's drag/resize interactions take precedence.

This is intentional for the floating-panel use case. A host that needs to programmatically
reposition the window must remount it (e.g., by changing the `key` prop).

### 3.3 Portal rendering

Window renders its entire subtree (backdrop + window div) via `ReactDOM.createPortal`
to `document.body`. This ensures correct z-index stacking above any host content
regardless of where `<Window>` appears in the component tree.

---

## 4. Events

| Callback | Payload | When fired |
|---|---|---|
| `onClose` | — | Click on the Close button |
| `onStateChange` | `'default' \| 'minimized' \| 'maximized'` | Any stage transition via Minimize or Maximize button |

**Stage toggle semantics:**
- Minimize button: `'default' → 'minimized'`; `'minimized' → 'default'` (restore)
- Maximize button: `'default' → 'maximized'`; `'maximized' → 'default'` (restore)

The `onStateChange` payload is the **next** state, not the previous. In controlled
mode the host receives the desired next state and may choose to ignore it.

---

## 5. Slots

Window uses two implicit ReactNode slots:

| Slot | Prop | Notes |
|---|---|---|
| Title bar content | `title` | Accepts any `ReactNode`; rendered in a `<span class="flex-1 text-sm font-medium truncate">`. The control buttons (Minimize/Maximize/Close) follow it in the title bar. |
| Body content | `children` | Rendered in `<div class="flex-1 overflow-auto p-3">`. Suppressed (not rendered at all) when `state === 'minimized'`. |

There is no explicit `actions` or `footer` slot in M1. Hosts compose action bars
inside `children`.

---

## 6. Component composition

- **Title bar control buttons** are internal to Window. The host controls their
  presence via the `closable` / `minimizable` / `maximizable` boolean props.
  Pointer-down events on the controls use `stopPropagation` to prevent the drag
  handler from intercepting them.
- **Backdrop** (`modal` mode) is a sibling div rendered via the same portal, at
  `z-index: 999` (one below the window at `z-index: 1000`).
- **Resize handles** (SE corner + S edge + E edge) are absolutely-positioned divs
  inside the window container, rendered only when `resizable && state === 'default'`.

---

## 7. Deferred features (M1 scope)

The following were not shipped in M1 and are tracked as expansion targets:

- `initialWidth` / `initialHeight` props (distinct from `width` / `height` controlled
  aliases — Kendo has both; we have only the combined prop)
- `onMove` callback for drag position updates
- `onResize` callback for resize size updates
- `shouldUpdateOnDrag` — defer position commit to drag-end vs. continuous
- `appendTo` — custom portal container (always `document.body` today)
- `autoFocus` on open
- `doubleClickStageChange` — title-bar double-click toggles fullscreen/maximized
- `overlayStyle` — structured style prop for the modal backdrop
- `WindowActionsBar` composition component
- Custom minimize/maximize/restore button slot (`minimizeButton` / `maximizeButton` /
  `restoreButton` Kendo pattern)
- `dir` RTL support
- Keyboard-driven move and resize (WAI-ARIA dialog/window pattern)
- `FULLSCREEN` stage (Kendo uses DEFAULT/MINIMIZED/FULLSCREEN; ours uses
  'default'/'minimized'/'maximized' — the 'maximized' state fills the viewport
  but is not necessarily true OS fullscreen)

---

## 8. Open questions (for council)

1. **Stage vocabulary alignment.** The implementation uses `'maximized'`; Kendo
   uses `FULLSCREEN`. Should we rename? Impact: tests + consumer code that checks
   `state === 'maximized'`. (Leaning: keep `'maximized'` for clarity;
   document the semantic mapping to Kendo's FULLSCREEN.)
2. **`top`/`left` vs. `initialTop`/`initialLeft` naming.** Kendo separates the
   controlled position (`left`, `top`) from the uncontrolled initial position
   (`initialLeft`, `initialTop`). Our current implementation uses `top`/`left`
   as mount-time-only seeds (§3.2). Should §FS-1.1 introduce the `initial*`
   names as synonyms or replacements?
3. **`WindowActionsBar` composition vs. slot props.** Should the expansion add a
   `WindowActionsBar` child component (Kendo pattern) or use the `minimizeButton` /
   `maximizeButton` / `restoreButton` render-prop approach?

---

## Full-surface expansion (Draft — waves 2-3, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/dialogs-popups.md`
Window section — 9 P1 capabilities unspecced; 4 P2 capabilities deferred.

**Supersedes:** the bullet list in §7 above — each item that falls in waves 2-3
is now specced below. §7 remains as a historical record of M1 deferrals.

---

### §FS-0 Explicitly out of scope (this contract)

| Audit row | Reason |
|---|---|
| `appendTo` custom portal container | P2; deferred past wave-3; `document.body` portal is sufficient for MVP |
| `shouldUpdateOnDrag` | P2; performance optimisation; deferred post-MVP |
| `dir` RTL support | P2; fleet-wide RTL pass deferred; no MVP requirement |

---

### §FS-1 Wave-2 — drag/resize/position surface

#### §FS-1.1 Controlled position + initial position aliases

Kendo exposes both a controlled pair (`left` / `top`) and an uncontrolled
initial pair (`initialLeft` / `initialTop`). Our M1 implementation uses `top` /
`left` as mount-time-only seeds. This section introduces the explicit separation.

```typescript
// New props (additive — existing top/left become aliases for initialTop/initialLeft):
initialLeft?: number    // uncontrolled initial left position (default 80); synonym for current `left`
initialTop?: number     // uncontrolled initial top position (default 80); synonym for current `top`
// The existing `left` and `top` props are deprecated aliases for initialLeft/initialTop.
// A future wave may add controlled left/top (live position prop) but that is deferred.
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `initialLeft` | `number` | `80` | Mount-time left position seed. Same as current `left`. |
| `initialTop` | `number` | `80` | Mount-time top position seed. Same as current `top`. |

**wave-2**

---

#### §FS-1.2 `onMove` callback

Fires continuously during drag (every `onPointerMove` update) with the current
`{ top, left }` position.

```typescript
// New prop:
onMove?: (position: { top: number; left: number }) => void
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `onMove` | `({ top, left }) => void` | — | Fired on each drag position update. |

**Behavioral rules:**
1. Fires only when `draggable === true` and `state === 'default'`.
2. Fires on every `onPointerMove` while a drag is in progress.
3. Does NOT fire on programmatic position resets.

**wave-2**

---

#### §FS-1.3 `onResize` callback

Fires continuously during resize (every `onPointerMove` during a resize) with
the current `{ width, height }` dimensions.

```typescript
// New prop:
onResize?: (size: { width: number; height: number }) => void
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `onResize` | `({ width, height }) => void` | — | Fired on each resize handle drag update. |

**Behavioral rules:**
1. Fires only when `resizable === true` and `state === 'default'`.
2. Width and height values are already clamped to `minWidth` / `minHeight`.
3. Does NOT fire when size is unchanged (clamp already at minimum).

**wave-2**

---

#### §FS-1.4 Drag bounds

Constrain the window's drag position to a bounding rect (typically the
viewport) so the user cannot drag the window entirely off-screen.

```typescript
// New prop:
dragBounds?: 'viewport' | { top?: number; left?: number; right?: number; bottom?: number }
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `dragBounds` | `'viewport' \| object` | omitted (no constraint) | Clamps the window position during drag. `'viewport'` uses `{ top: 0, left: 0, right: window.innerWidth, bottom: window.innerHeight }`. Object form allows custom bounds. |

**Behavioral rules:**
1. Clamping is applied every `onPointerMove` during drag.
2. `'viewport'` uses the `window.innerWidth` / `window.innerHeight` at the moment of
   each move event (not cached at drag-start) so viewport resizes are reflected.
3. The window can still be partially off-screen when bounds allow only the title
   bar to remain visible; this is intentional (title bar drag target must stay
   reachable). Minimum visible strip is not enforced by the contract (host's
   responsibility via `dragBounds` values).

**wave-2**

---

### §FS-2 Wave-3 — stage UX + composition

#### §FS-2.1 `FULLSCREEN` stage (Kendo `doubleClickStageChange`)

Add FULLSCREEN as a distinct stage concept AND the double-click title-bar
affordance. In our vocabulary `'maximized'` already fills the viewport; this
section clarifies the semantic mapping and adds the double-click trigger.

```typescript
// No new WindowState value is added — 'maximized' maps to Kendo's FULLSCREEN.
// New prop:
doubleClickStageChange?: boolean   // default true (Kendo default)
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `doubleClickStageChange` | `boolean` | `true` | When `true`, double-clicking the title bar toggles between `'default'` and `'maximized'`. Fires `onStateChange` on the transition. |

**Behavioral rules:**
1. Double-click on the title bar only — NOT on control buttons.
2. Fires `onStateChange('maximized')` when current state is `'default'`; fires
   `onStateChange('default')` when current state is `'maximized'`.
3. No effect when `state === 'minimized'` (consistent with Kendo).
4. Disabled when `doubleClickStageChange === false`.

**wave-3**

---

#### §FS-2.2 `autoFocus` on open

```typescript
// New prop:
autoFocus?: boolean   // default true (Kendo default)
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `autoFocus` | `boolean` | `true` | When `true`, the window container (or first focusable element inside it) receives focus on mount. |

**Behavioral rules:**
1. When `autoFocus === true`, call `containerRef.current?.focus()` in a
   `useEffect(() => { ... }, [])`.
2. If the body contains a focusable element (button, input, anchor), the browser's
   default focus-first-focusable behaviour applies; the `tabIndex="-1"` on the
   container is a fallback for content-free windows.
3. Does not apply on re-renders or stage transitions — only on mount.

**wave-3**

---

#### §FS-2.3 `WindowActionsBar` composition component

A thin layout wrapper for the title-bar control area. Enables hosts to render
custom action buttons alongside (or replacing) the default Minimize/Maximize/Close
controls.

```typescript
export interface WindowActionsBarProps {
  children: React.ReactNode
}

export function WindowActionsBar({ children }: WindowActionsBarProps): JSX.Element
```

**Usage pattern:**

```tsx
<Window title="My Panel" minimizable={false} maximizable={false}>
  <WindowActionsBar>
    <button aria-label="Settings">⚙</button>
    <button aria-label="Help">?</button>
  </WindowActionsBar>
  {/* body content */}
</Window>
```

**Behavioral rules:**
1. `WindowActionsBar` renders its children in the title-bar control area, **before**
   the built-in Minimize/Maximize/Close buttons.
2. Pointer-down on `WindowActionsBar` content stops propagation to the drag handler
   (same treatment as the existing controls container).
3. The built-in buttons remain unless suppressed via `closable={false}` etc.
4. Only ONE `WindowActionsBar` child is supported per Window in wave-3; multiple
   instances are a future concern.

**wave-3**

---

#### §FS-2.4 Custom minimize/maximize/restore button slots

Kendo's `minimizeButton` / `maximizeButton` / `restoreButton` pattern. Render-
prop approach — each accepts a React component type that receives `onClick` and
`aria-label`.

```typescript
// New props:
minimizeButton?: React.ComponentType<React.ButtonHTMLAttributes<HTMLButtonElement>>
maximizeButton?: React.ComponentType<React.ButtonHTMLAttributes<HTMLButtonElement>>
restoreButton?: React.ComponentType<React.ButtonHTMLAttributes<HTMLButtonElement>>
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `minimizeButton` | `ComponentType<ButtonHTMLAttributes>` | built-in `<button>` | Custom Minimize button. Receives `onClick` (wired to state transition) and `aria-label="Minimize"`. |
| `maximizeButton` | `ComponentType<ButtonHTMLAttributes>` | built-in `<button>` | Custom Maximize button. Receives `onClick` and `aria-label="Maximize"`. |
| `restoreButton` | `ComponentType<ButtonHTMLAttributes>` | `maximizeButton` fallback | Custom Restore button shown when state is `'maximized'` (replaces Maximize). Receives `onClick` and `aria-label="Restore"`. |

**Behavioral rules:**
1. When a custom component is supplied, it replaces the built-in button entirely.
2. The `minimizable` / `maximizable` props still control whether the button is
   rendered at all — the custom component is suppressed when the corresponding
   flag is `false`.
3. Custom components MUST forward their `onClick` and `aria-label` props to a
   focusable element to maintain keyboard and screen-reader accessibility.

**wave-3**

---

#### §FS-2.5 Modal overlay style prop

```typescript
// New prop:
overlayStyle?: React.CSSProperties
```

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `overlayStyle` | `React.CSSProperties` | `{ background: 'rgba(0,0,0,0.3)' }` (from token `--sf-window-backdrop`) | Inline style merged onto the modal backdrop div. Useful for custom backdrop colours or blur effects. |

**Behavioral rules:**
1. Only applied when `modal === true`.
2. Merged with (overrides) the Tailwind class `bg-black/30`.

**wave-3**
