# UserMenu — Interaction Contract

- **Component:** UserMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted — amended per ADR 0121 Appendix A (2026-06-15)
- **Companion contracts:** [Semantic](./UserMenu.Semantic.md) · [Interaction](./UserMenu.Interaction.md) · [Accessibility](./UserMenu.Accessibility.md) · [Styling](./UserMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/UserMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation); amended ADR 0121 Phase 1

---

## 1. Trigger button interactions

| Trigger | Effect |
|---|---|
| Click trigger button | Toggles `open` (open → close, close → open) |
| `Escape` while popup has focus | Radix closes the popup and returns focus to the trigger |

Open state is controlled by `Popover`. Radix supplies Escape handling, outside interaction,
collision-aware positioning, and focus restoration.

---

## 2. Dropdown close triggers

| Trigger | Effect |
|---|---|
| Click outside popup | Radix closes the popover |
| Click any item where `closeOnSelect !== false` | Calls handler then sets `open = false` |
| Click item where `closeOnSelect: false` | Calls handler; **menu stays open** |
| Click sign-out item | Calls `onSignOut()` then sets `open = false` |

**`closeOnSelect` rule (ADR 0121 §Appendix A):**

An item keeps the menu open after interaction when `closeOnSelect: false` is set OR when `kind: 'custom'` and `render` is present. This is the seam for inline interactive controls (e.g. a theme toggle, a "Bridge: Connected" status row) that should remain usable without reopening the menu.

The default (`closeOnSelect: true` or omitted) closes the menu on click — matching the M1 behaviour.

---

## 3. Custom render items (`render` prop)

When `UserMenuItem.render` is provided:
- The item renders `render` as its entire content (label and icon are not rendered).
- The menu stays open after interaction (implicit `closeOnSelect: false`).
- The item is wrapped in a presentational `<div role="none">` container.
- The trigger and popup advertise `dialog`, not `menu`, because the popup contains arbitrary native or composite controls.
- Ordinary action items in the same popup retain native link/button semantics.
- The rendered control owns its ARIA roles and keyboard model. The shipped theme selector uses a horizontal `radiogroup` with roving-tabindex `radio` children; arrows wrap and Home/End select the first/last option.

This slot is used for the inline theme control and the Bridge connection status row (ADR 0121 Phase 1 use cases).

---

## 4. Chevron indicator

On medium+ viewports (`hidden md:block`), a chevron icon rotates `rotate-180` when `open`. This is a visual indicator only (decorative; no `aria-hidden` attribute needed — no role or text content).

---

## 5. Responsive trigger display

On small viewports (below `md` breakpoint), the trigger shows only the avatar circle. Name and role text are hidden (`hidden md:flex`). The chevron is hidden (`hidden md:block`). The avatar circle itself is always visible.

---

## 6. Placement

| `placement` prop | Dropdown position | Implementation |
|---|---|---|
| `'bottom'` (default) | Below trigger | `top-full mt-2` |
| `'top'` | Above trigger | `bottom-full mb-2` |

`placement="top"` is designed for sidebar footer placement (per ADR 0121 §D8 settings consolidation). The menu opens upward to avoid being clipped by the viewport bottom.

---

## 7. Popup lifecycle

`Popover` owns document-level interaction listeners only for its open lifecycle. Closing via
Escape restores focus to the trigger.

---

## 8. Known gaps

| Gap | Description |
|---|---|
| No keyboard navigation within dropdown | Arrow keys do not navigate between menu items within the open dropdown. |
