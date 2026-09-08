# Window — Styling Contract

- **Component:** Window
- **ADR 0017 family:** Layout
- **Contract type:** Styling (tokens, Tailwind recipes, visual states)
- **Status:** Accepted (shipped surface) + Draft (expansion sections §FS-*)
- **Companion contracts:** [Semantic](./Window.Semantic.md) · [Interaction](./Window.Interaction.md) · [Accessibility](./Window.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Window.tsx`
- **Catalog row:** #149 Window (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation) + Draft expansion

---

## 1. Token surface

| Token | Semantic role |
|---|---|
| `--sf-window-bg` | Window body background (`card`) |
| `--sf-window-border` | Window border (`border`) |
| `--sf-window-titlebar-bg` | Title bar background (`muted/30`) |
| `--sf-window-shadow` | Window shadow (`shadow-xl`) |
| `--sf-window-backdrop` | Modal backdrop (`bg-black/30`) |

---

## 2. Tailwind class recipes

### 2.1 Window container `<div>`

`flex flex-col rounded-lg border border-border bg-card shadow-xl overflow-hidden`

Maximized: `rounded-none` (override)

Inline styles: `position: fixed`, `top`, `left`, `width`, `height`, `zIndex: 1000`

### 2.2 Title bar `<div>`

`flex items-center gap-2 px-3 py-2 bg-muted/30 border-b border-border shrink-0`

When draggable + default state: `cursor-move select-none`

### 2.3 Title text `<span>`

`flex-1 text-sm font-medium truncate`

### 2.4 Title bar controls container `<div>`

`flex items-center gap-1`

### 2.5 Title bar buttons (Minimize/Maximize)

`h-6 w-6 rounded hover:bg-accent flex items-center justify-center text-xs`

`h-6 w-6` = 24 × 24 px, meeting the WCAG 2.5.8 (Level AA) target-size minimum. Previously `h-5 w-5` (20 × 20 px) was below the minimum. The icon inside remains `text-xs`; the larger container provides the compliant touch/click target.

### 2.6 Close button

`h-6 w-6 rounded hover:bg-destructive hover:text-destructive-foreground flex items-center justify-center text-xs`

### 2.7 Body `<div>`

`flex-1 overflow-auto p-3`

### 2.8 Resize handles

SE corner: `absolute bottom-0 right-0 w-4 h-4 cursor-se-resize`  
S edge: `absolute bottom-0 left-0 right-4 h-1 cursor-s-resize`  
E edge: `absolute right-0 top-8 bottom-1 w-1 cursor-e-resize`

### 2.9 Modal backdrop

`fixed inset-0 bg-black/30 z-[999]`

---

## 3. Visual state inventory (Accepted)

| State | Visual treatment |
|---|---|
| `default` | Normal floating panel; shadow-xl; rounded-lg |
| `minimized` | Title bar only; body `display: none`; no resize handles; same shadow |
| `maximized` | `position: fixed; inset: 0; width: 100vw; height: 100vh; rounded-none` |
| `dragging` | `cursor-move select-none` on title bar (applied when `draggable && state === 'default'`) |
| `resizing (SE)` | `cursor-se-resize` on the SE handle div |
| `resizing (S)` | `cursor-s-resize` on the S edge div |
| `resizing (E)` | `cursor-e-resize` on the E edge div |
| `modal overlay` | Backdrop `bg-black/30` rendered at `z-[999]`; window at `z-[1000]` |

---

## 4. Known gap — control button size (M1)

The shipping implementation uses `h-5 w-5` (20×20 px) for Minimize/Maximize/Close
buttons. This is below the WCAG 2.2 SC 2.5.8 minimum touch/click target of 24×24 px.

**Contract target:** `h-6 w-6` (24×24 px) per §2.4–2.6 above.

The implementation must be updated before the next WCAG audit. The contract documents
the correct target; the current implementation has the known gap.

---

## Full-surface expansion (Draft — waves 2-3, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

---

### §FS-1 Wave-2 — minimized visual state

#### §FS-1.1 Minimized state width

In M1, the minimized window retains its last `width`. The wave-2 refinement
specifies that minimized windows should collapse to a fixed title-bar-only width
so multiple minimized windows tile predictably.

```
Minimized width: min(currentWidth, 240px)
```

The `240px` constant is a design token candidate: `--sf-window-minimized-width`.
Until the token is minted, `w-60` (240px) is the Tailwind class to apply when
`state === 'minimized'`.

**New Tailwind recipe for minimized container:**

`flex flex-col rounded-lg border border-border bg-card shadow-xl overflow-hidden w-60`

(Override: drop the inline `style.width`; apply `w-60` class instead when minimized.)

**wave-2**

---

### §FS-2 Wave-3 — focus ring + backdrop blur

#### §FS-2.1 Title bar keyboard-focus ring

When `draggable === true` and the title bar is focusable via keyboard (Accessibility
§FS-2.2), a focus ring MUST be visible.

**Token:**
| Token | Semantic role |
|---|---|
| `--sf-window-focus-ring` | Focus ring color on title bar (`ring-2 ring-ring ring-offset-1`) |

**Tailwind recipe addition for title bar when focused:**
`focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1`

Applied to the title bar `<div>` (requires `tabIndex={0}` from Accessibility §FS-2.2).

**wave-3**

---

#### §FS-2.2 Resize handle focus ring

Each keyboard-focusable resize handle (Accessibility §FS-2.3) MUST show a
visible focus ring.

**Tailwind recipe for resize handles (keyboard-focusable):**
- SE corner: `absolute bottom-0 right-0 w-4 h-4 cursor-se-resize focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:rounded-sm`
- S edge: `absolute bottom-0 left-0 right-4 h-2 cursor-s-resize focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring` (height increased from `h-1` to `h-2` for reachable focus target)
- E edge: `absolute right-0 top-8 bottom-1 w-2 cursor-e-resize focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring` (width increased from `w-1` to `w-2`)

**wave-3**

---

#### §FS-2.3 Modal backdrop blur (optional enhancement)

An optional `backdropBlur` variant for the modal backdrop, controlled via the
`overlayStyle` prop (Semantic §FS-2.5) or a dedicated styling token.

**Token (additive):**
| Token | Semantic role |
|---|---|
| `--sf-window-backdrop-blur` | Backdrop blur amount (default `0`; can be set to e.g. `4px`) |

**Tailwind recipe variant:**
`fixed inset-0 bg-black/30 z-[999] backdrop-blur-sm`

Applied when `overlayStyle` includes `backdropFilter` or when a future
`backdropBlur?: boolean` prop is added. Not shipped in wave-3 — documented as
a design-system enhancement target.

**wave-3 (optional; design discretion)**
