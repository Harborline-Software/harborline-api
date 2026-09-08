# ContextMenu — Accessibility Contract

- **Component:** ContextMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ContextMenu.Semantic.md) · [Interaction](./ContextMenu.Interaction.md) · [Styling](./ContextMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ContextMenu.tsx`
- **Catalog row:** #34 ContextMenu (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="menu"` | Menu container `<div>` | WAI-ARIA menu |
| `aria-label="Context menu"` | Menu container | Accessible name |
| `role="menuitem"` | Each item `<button>` | Menu item role |
| `tabIndex={-1}` | Each item button | Removed from tab order; focus managed by JS |
| `disabled` | Disabled item | Native HTML disabled |
| `role="separator"` | Group divider | Separator between groups |

---

## 2. Focus management

Items receive focus programmatically via `.focus()` when `activeIdx` changes. Items have `tabIndex={-1}` so they are not in the natural tab order.

The trigger area has no ARIA attributes to indicate a context menu is available — keyboard users may not know right-click is supported.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CM4 | High | No keyboard mechanism to open context menu for keyboard-only users (right-click is pointer-only; no Shift+F10 or Menu key handler) | Accepted-risk M1 |
| G-CM5 | Medium | Focus not returned to content area on close | Accepted-risk M1 |
| G-CM6 | Low | No `aria-haspopup="menu"` on the wrapper to announce context menu availability to AT | Accepted-risk M1 |
