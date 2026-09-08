# ActionMenu — Interaction Contract

- **Component:** ActionMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ActionMenu.Semantic.md) · [Accessibility](./ActionMenu.Accessibility.md) · [Styling](./ActionMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/ActionMenu.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

Trigger click toggles `open` state. Container `onClick` handles this; custom `trigger` renders inside the container and bubbles clicks up.

---

## 2. Click-outside dismiss

`mousedown` listener on `document`. Fires `setOpen(false)` when event target is outside `containerRef.current`. Listener added on open, removed on close.

---

## 3. Item click

Clicking an item calls `item.onClick()` then closes the menu (`setOpen(false)`). Disabled items do not fire `onClick`.

---

## 4. Keyboard behavior

No explicit keyboard navigation implemented in the reference. Menu opens on Enter/Space via the default trigger button's native button behavior. Arrow-key navigation within the menu is not implemented (G-ACTMENU1).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ACTMENU1 | High | No arrow-key navigation within menu — WCAG 2.1 SC 2.1.1 requires keyboard-only operation | Accepted-risk M1 |
| G-ACTMENU2 | High | No Escape-key close — expected pattern for `role="menu"` | Accepted-risk M1 |
| G-ACTMENU3 | Medium | No focus return to trigger on close | Accepted-risk M1 |
