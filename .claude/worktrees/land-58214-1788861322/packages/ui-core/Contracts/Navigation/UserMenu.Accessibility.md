# UserMenu — Accessibility Contract

- **Component:** UserMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted — amended for custom controls (2026-07-20)
- **Companion contracts:** [Semantic](./UserMenu.Semantic.md) · [Interaction](./UserMenu.Interaction.md) · [Accessibility](./UserMenu.Accessibility.md) · [Styling](./UserMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/UserMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation); PAO amendment pending (ADR 0121 Phase 1)

---

> **ADR 0121 Appendix A accessibility amendments:**
> - `aria-hidden` on the avatar circle `<img>` / initials element when used alongside a visible name in the trigger (D8 avatar consolidation — the avatar is decorative when name is also announced)
> - `placement="top"` has no ARIA semantics change but PAO should confirm the positioning doesn't trap screen reader focus in a confusing order
> - `render` custom items with interactive child content use a `role="none"` layout wrapper. The rendered control owns its native or composite-widget semantics.
> - A theme selector rendered as a custom item uses a `radiogroup` with roving-tabindex `radio` children.

---

## 1. ARIA roles and attributes

| Element | Role / Attribute | Value |
|---|---|---|
| Trigger `<button>` | `aria-haspopup` | `"menu"` for action-only content; `"dialog"` when any custom control is rendered |
| Trigger `<button>` | `aria-expanded` | `true` when open, `false` when closed |
| Trigger `<button>` | `aria-controls` | `menuId` (matches dropdown `id`) |
| Trigger `<button>` | `aria-label` | `"User menu for {name}"` |
| Dropdown `<div>` | `role` | `menu` for action-only content; `dialog` when any custom control is rendered |
| Dropdown `<div>` | `aria-label` | `"User menu"` |
| Dropdown `<div>` | `id` | `menuId` (from `useId()`) |
| `<a>` / `<button>` action items | `role` | `menuitem` in the action-only menu; native semantics in the custom-control dialog |
| Custom-item wrapper `<div>` | `role` | `none`; the rendered child owns its semantics |
| Divider `<div>` | `role` | `separator` |
| Avatar `<img>` | `alt` | `name` |
| Chevron `<svg>` | `aria-hidden` | (implicit — no role, decorative) |
| Icon `<span>` | `aria-hidden` | `"true"` |

---

## 2. Screen reader behaviour

- Trigger announces `"User menu for [name]"` as its accessible name, `expanded`/`collapsed` state, and the truthful `menu` or `dialog` popup type.
- `aria-controls` links the trigger to the dropdown by id.
- An action-only dropdown is a `menu` with `menuitem` actions. A dropdown containing a custom control is a `dialog`; ordinary links and buttons retain native semantics, and each custom control owns its own accessible pattern.
- Radix Popover closes the popup on `Escape` and restores focus to the trigger.
- The theme selector uses a horizontal `radiogroup`. Only the selected `radio` is in the Tab order; arrow keys, Home, and End move selection and focus.
- Divider items carry `role="separator"` as expected.
- Avatar image has `alt={name}` for meaningful alt text when an image loads.
- Icon strings (`item.icon`) carry `aria-hidden="true"` — labels provide accessible names.

---

## 3. Known gaps

| Gap | Severity | Description |
|---|---|---|
| No arrow-key navigation within menu | Medium | WAI-ARIA `menu` pattern requires `ArrowDown`/`ArrowUp` to move focus between `menuitem` elements. Currently all items are reachable only via Tab. |
| No roving tabindex | Medium | All menu items are in the Tab order. The menu pattern requires only one item focusable at a time (roving tabindex), with arrows to move between them. |
| Info header not aria-annotated | Low | The name/email/role info block at the top of the dropdown has no ARIA grouping or `aria-label`. Screen readers read it as plain text between menu items, which may be confusing. |
