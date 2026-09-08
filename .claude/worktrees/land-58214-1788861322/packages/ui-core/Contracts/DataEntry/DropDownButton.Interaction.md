# DropDownButton — Interaction Contract

- **Component:** DropDownButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownButton.Semantic.md) · [Accessibility](./DropDownButton.Accessibility.md) · [Styling](./DropDownButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DropDownButton.tsx`
- **Catalog row:** #47 DropDownButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED
  → click trigger → OPEN
  → disabled → no-op

OPEN
  → click trigger → CLOSED
  → click item (enabled) → item.onClick() → CLOSED
  → click item (disabled) → no-op (stays OPEN)
  → click outside (document mousedown on non-component element) → CLOSED
  → Escape key → CLOSED
```

---

## 2. Click-outside detection

Implemented via `document.addEventListener('mousedown', handler)` on mount; removed on unmount. The handler checks whether the click target is inside the component ref; if not, closes the menu.

---

## 3. Keyboard behavior

| Key | Context | Action |
|---|---|---|
| Enter / Space | Trigger focused | Toggle menu open/closed |
| Escape | Menu open | Close menu, return focus to trigger |
| ArrowDown | Menu open | Move focus to next item |
| ArrowUp | Menu open | Move focus to previous item |
| Enter | Item focused | Activate item → close menu |
| Tab | Menu open | Close menu, advance focus in document |

---

## 4. Disabled state

When `disabled=true`, the trigger button is inert — click and keyboard events have no effect. Menu cannot be opened.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DDB1 | Low | Arrow key navigation between menu items not implemented in M1 — items are not individually focusable | Accepted-risk M1; click-only navigation for now |
| G-DDB2 | Low | No Home/End key support to jump to first/last item | Accepted-risk M1 |
