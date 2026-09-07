# Tour — Styling Contract

- **Component:** Tour / GuidedWalkthrough
- **ADR 0017 family:** Overlays
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tour.Semantic.md) · [Interaction](./Tour.Interaction.md) · [Accessibility](./Tour.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Ant Design Tour baseline)
- **Catalog row:** #A7 Tour / GuidedWalkthrough (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Tour baseline)

---

## 1. Mask overlay

`fixed inset-0 z-[1000]` with spotlight cutout exposing the target element.

**⚠ Pointer-events constraint:** CSS `clip-path` or SVG `<clipPath>` on a full-screen overlay creates a *visual* hole but does NOT pass pointer events through the overlay DOM element. If the Tour spec requires users to click the highlighted element (interactive spotlight), a single overlay with `clip-path` will silently swallow all clicks on the exposed area.

**Required approach — four-rectangle donut layout:**

Use four separate `fixed` overlay rectangles instead of one clipped overlay:

```
┌────────────────────────────────────────┐  ← top overlay (0, 0, fullW, targetTop)
│                  top                   │
├───────────────┬────────────┬───────────┤
│     left      │  spotlight │   right   │
│               │  (target)  │           │
├───────────────┴────────────┴───────────┤
│                 bottom                 │
└────────────────────────────────────────┘  ← bottom overlay
```

Each of the four panels is `bg-black/50`. The "donut hole" (spotlight area) is physically absent from the DOM — pointer events reach the target element naturally.

```tsx
// Computed from target.getBoundingClientRect() + scroll offsets:
const { top, left, width, height } = targetRect
// Top panel
<div class="fixed inset-0 bg-black/50" style={{ bottom: `${window.innerHeight - top}px` }} />
// Bottom panel
<div class="fixed inset-0 bg-black/50" style={{ top: `${top + height}px` }} />
// Left panel
<div class="fixed bg-black/50" style={{ top, left: 0, width: left, height }} />
// Right panel
<div class="fixed bg-black/50" style={{ top, left: left + width, right: 0, height }} />
```

**Alternative (non-interactive spotlight):** If the highlighted element does NOT need to be clickable (the Tour only highlights, not allows interaction), use a single `clip-path` overlay with `pointer-events: none` on the *entire* overlay. The target element will then be unreachable by pointer. Only use this when `maskClosable={false}` and interactive-spotlight is not required.

Base overlay classes: `fixed inset-0 bg-black/50 z-[1000]`

---

## 2. Popover container

`absolute bg-popover text-popover-foreground rounded-md shadow-lg border border-border p-4 w-72 z-[1001]`

Positioned via `placement` prop relative to target bounding rect. Fallback: viewport-centered when no target.

---

## 3. Arrow

CSS triangle or SVG arrow pointing from popover toward the highlighted element. `fill-popover border-border`. Size: 8px.

---

## 4. Popover sections

Cover image area: `w-full rounded-sm overflow-hidden mb-3` (optional, above title).

Title: `text-sm font-semibold text-foreground mb-1`.

Description: `text-sm text-muted-foreground`.

---

## 5. Step indicators

`flex gap-1.5 items-center`. Each dot: `size-1.5 rounded-full`. Active: `bg-primary`. Inactive: `bg-muted-foreground/40`. Clickable dots add `cursor-pointer hover:bg-muted-foreground/70`.

---

## 6. Navigation buttons

Footer: `flex items-center justify-between mt-4`.

Previous: `Button variant="ghost" size="sm"`. Next/Finish: `Button variant="default" size="sm"`. Close `×`: `Button variant="ghost" size="icon"` absolute top-right.

---

## 7. Type variants

`type="default"`: uses `bg-popover` (above).
`type="primary"`: uses `bg-primary text-primary-foreground` for popover background.

---

## 8. Design tokens

Uses: `bg-popover`, `text-popover-foreground`, `border-border`, `bg-primary`, `text-primary-foreground`, `text-foreground`, `text-muted-foreground`, `bg-muted-foreground`.
