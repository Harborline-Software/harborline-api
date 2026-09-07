# ContextMenu — Interaction Contract

- **Component:** ContextMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ContextMenu.Semantic.md) · [Accessibility](./ContextMenu.Accessibility.md) · [Styling](./ContextMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ContextMenu.tsx`
- **Catalog row:** #34 ContextMenu (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED (pos=null)
  → right-click within wrapper → OPEN at (clientX, clientY)

OPEN (pos={x,y})
  → click item (enabled) → item.onSelect() → CLOSED
  → click item (disabled) → no-op
  → mousedown outside menu → CLOSED
  → Escape key → CLOSED
  → ArrowDown → activeIdx++
  → ArrowUp → activeIdx-- (clamped at 0)
  → Enter / Space (activeIdx >= 0 and item enabled) → item.onSelect() → CLOSED
```

---

## 2. Right-click handler

`e.preventDefault()` suppresses the browser native context menu. `e.stopPropagation()` prevents parent elements from also receiving the event. Menu position is recorded as `{x: e.clientX, y: e.clientY}`.

---

## 3. Click-outside detection

`document.addEventListener('mousedown', ...)` registered when the menu is open; removed when it closes. Handler checks `menuRef.current.contains(target)`.

---

## 4. Keyboard navigation

Keyboard listeners are registered globally on `document` when the menu is open (also removed on close). Arrow keys navigate `activeIdx` across the flat item list. When `activeIdx >= 0`, the active item's DOM element receives focus via:
```
menuRef.current.querySelectorAll('[role="menuitem"]')[activeIdx].focus()
```

---

## 5. Focus after close

When the menu closes via Escape or item activation, focus is NOT explicitly returned to the triggering element. Focus state is lost in M1.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CM1 | Medium | No viewport-edge collision detection — menu may clip at screen edges | Accepted-risk M1 |
| G-CM2 | Medium | Focus not returned to trigger element on close | Accepted-risk M1 |
| G-CM3 | Low | `activeIdx` starts at -1; first ArrowDown moves to 0 but first ArrowUp goes to 0 too (clamp at 0) | Minor UX quirk; Accepted-risk M1 |
