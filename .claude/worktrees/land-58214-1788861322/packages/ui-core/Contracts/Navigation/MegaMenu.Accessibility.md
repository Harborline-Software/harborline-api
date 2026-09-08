# MegaMenu — Accessibility Contract

- **Component:** MegaMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MegaMenu.Semantic.md) · [Interaction](./MegaMenu.Interaction.md) · [Accessibility](./MegaMenu.Accessibility.md) · [Styling](./MegaMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/MegaMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Element | Role / Attribute | Value |
|---|---|---|
| `<nav>` | `role` | `menubar` |
| Trigger `<button>` | `role` | `menuitem` |
| Trigger `<button>` | `aria-haspopup` | `"true"` |
| Trigger `<button>` | `aria-expanded` | `true` when its panel is active, `false` otherwise |
| Panel `<div>` | `role` | `menu` |
| Link `<a>` / `<button>` | `role` | `menuitem` |
| Chevron `<span>` | `aria-hidden` | `"true"` |
| Icon `<span>` | `aria-hidden` | `"true"` |

---

## 2. Keyboard accessibility

| Key | Behaviour |
|---|---|
| `Tab` | Browser default; moves focus through trigger buttons, then into the open panel links in DOM order |
| `Enter` / `Space` | Activates the focused trigger button (opens/closes panel) or activates a focused link |
| `Escape` | Closes the active panel |

Arrow-key navigation within the `menubar` and between `menuitem` elements inside the panel is not implemented (see Known gaps).

---

## 3. Screen reader behaviour

- The `<nav role="menubar">` landmark is announced as a menu bar.
- Each trigger is announced as a `menuitem` with `aria-expanded` state.
- The panel `role="menu"` and its `role="menuitem"` children are announced as menu structure.
- Icons and chevrons are `aria-hidden="true"` — decorative only.
- Column heading `<p>` elements are not linked to their group; screen readers encounter them as plain text between menu items.

---

## 4. Known gaps

| Gap | Severity | Description |
|---|---|---|
| No `aria-owns` link between trigger and panel | Medium | `aria-haspopup="true"` is set but the trigger button does not use `aria-owns` or `aria-controls` to associate it with the panel element. |
| No arrow-key navigation within `menu` | Medium | WAI-ARIA menu pattern requires `ArrowDown`/`ArrowUp` to move between `menuitem`s within the open `menu`. This is unimplemented. |
| No `roving tabindex` on trigger bar | Medium | `menubar` pattern requires `roving tabindex` (only one trigger focusable at a time, arrows move between them). Currently all triggers are in the natural tab order. |
| Column titles not `group`/`aria-labelledby` | Low | Column heading `<p>` elements are not marked up as group labels for their link groups. |
| `aria-haspopup="true"` vs `"menu"` | Low | `aria-haspopup="true"` is semantically equivalent to `"menu"` in ARIA 1.1+, but explicit `"menu"` is more precise. |
