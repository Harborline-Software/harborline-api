# NavDrawer — Accessibility Contract

- **Component:** NavDrawer
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NavDrawer.Semantic.md) · [Interaction](./NavDrawer.Interaction.md) · [Accessibility](./NavDrawer.Accessibility.md) · [Styling](./NavDrawer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NavDrawer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Element | Role / Attribute | Value |
|---|---|---|
| Drawer panel `<div>` | `role` | `dialog` |
| Drawer panel `<div>` | `aria-modal` | `"true"` |
| Drawer panel `<div>` | `aria-label` | `"Navigation menu"` |
| Drawer panel `<div>` | `tabIndex` | `-1` (receives programmatic focus) |
| Backdrop `<div>` | `aria-hidden` | `"true"` |
| Close button | `aria-label` | `"Close menu"` |
| Active item | `aria-current` | `"page"` |
| Icons `<span>` | `aria-hidden` | `"true"` |

---

## 2. Keyboard accessibility

| Key | Behaviour |
|---|---|
| `Escape` | Closes drawer via `onClose()` |
| `Tab` | Moves focus through drawer content in DOM order (no focus trap) |
| `Enter` / `Space` | Activates focused link or button |

---

## 3. Screen reader behaviour

- Drawer is announced as a `dialog` landmark with name `"Navigation menu"` when it opens and receives focus.
- `aria-modal="true"` signals to screen readers that content behind the dialog is inert. However, without a programmatic focus trap, this signal alone is not sufficient — some screen readers will still allow navigation outside the dialog.
- Items with `active` flag announce `aria-current="page"` to signal the current page.
- Icons and badge content are `aria-hidden="true"` — label text provides the accessible name for each item.
- `<nav>` inside the drawer provides a navigation landmark that AT users can navigate to.

---

## 4. Known gaps

| Gap | Severity | Description |
|---|---|---|
| No focus trap | High | Without a focus trap, keyboard users can Tab out of the open dialog into page content. `aria-modal="true"` is not universally honoured without inert or focus-trap polyfill. |
| Focus not returned on close | Medium | When the drawer closes, focus is not returned to the trigger that opened it (e.g. the hamburger button). Screen reader users lose their place. |
| No scroll lock | Low | Page behind the backdrop can be scrolled while the drawer is open, which may confuse users. |
| Badge labels | Low | Badge values (`badge?: string | number`) are rendered inside a `<span>` with no accessible text. If the badge conveys meaningful state (e.g. unread count), an `aria-label` or `sr-only` annotation is needed. |
