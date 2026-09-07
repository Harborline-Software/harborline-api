# DropDownButton — Accessibility Contract

- **Component:** DropDownButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownButton.Semantic.md) · [Interaction](./DropDownButton.Interaction.md) · [Styling](./DropDownButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DropDownButton.tsx`
- **Catalog row:** #47 DropDownButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<button type="button">` | Trigger button | Text from `text` prop |
| `aria-haspopup="menu"` | Trigger button | Indicates a menu popup. The explicit `menu` token is required — it matches the `role="menu"` container below and is more precise than the generic `true`, which WAI-ARIA treats as a synonym for `menu` but which does not state the popup kind. (Amended per PR 2728; the shipped implementation uses `menu`.) |
| `aria-expanded={open}` | Trigger button | Reflects open/closed state |
| `aria-controls={menuId}` | Trigger button | Links the trigger to the menu id (`packages/ui-react/src/components/buttons/DropDownButton.tsx:83,117,127`) |
| `role="menu"` | Dropdown container | WAI-ARIA menu role |
| `role="menuitem"` | Each item | Standard menu item role |
| `aria-disabled="true"` | Disabled item | Marks disabled items |

---

## 2. Focus management

When the menu opens, focus moves to the first enabled menu item. `ArrowUp` from the trigger opens
the menu and focuses its last enabled item. Disabled items are excluded from focus movement
(`packages/ui-react/src/components/buttons/useMenuKeyboardNavigation.ts:17-28,35-39,52-63`).

`Escape` from the trigger or a menu item closes the menu and returns focus to the trigger. An
enabled item activation also returns focus to the trigger. `Tab` closes the menu without restoring
focus so the browser can continue its natural tab sequence
(`packages/ui-react/src/components/buttons/useMenuKeyboardNavigation.ts:41-45,93-99`;
`packages/ui-react/src/components/buttons/DropDownButton.tsx:92-96`).

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DDB3 | — | Trigger sets `aria-controls={menuId}` and the popup menu uses that id (`packages/ui-react/src/components/buttons/DropDownButton.tsx:83,117,127`) | Implemented in the shipped reference implementation |
| G-DDB4 | — | Enabled items receive managed focus; ArrowUp/ArrowDown wrap, Home/End jump to a boundary, and printable-key typeahead moves focus (`packages/ui-react/src/components/buttons/DropDownButton.tsx:128-145`; `packages/ui-react/src/components/buttons/useMenuKeyboardNavigation.ts:17-28,65-117,119-122`) | Implemented in the shipped reference implementation |
| G-DDB5 | Low | Trigger exposes no `aria-label` prop; its accessible name comes from the required `text` content (`packages/ui-react/src/components/buttons/DropDownButton.tsx:18-21,109-123`) | Open contract limit; icon-only presentation is unsupported, so consumers must provide non-empty `text` |
| G-DDB6 | Low | Direct regression coverage locks ArrowDown, disabled-item skipping, Escape closure, and focus restoration, but not every menu-id, Home/End, typeahead, or Tab path (`packages/ui-react/src/components/buttons/__tests__/DropDownButton.test.tsx:205-224`) | Follow-up test debt; no test changes are authorized by this documentation card |
