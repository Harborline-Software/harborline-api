# NavDrawer — Semantic Contract

- **Component:** NavDrawer
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NavDrawer.Interaction.md) · [Accessibility](./NavDrawer.Accessibility.md) · [Styling](./NavDrawer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NavDrawer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled off-canvas navigation drawer

---

## 1. Component purpose

**NavDrawer** — a slide-in navigation panel (drawer/sidebar) that overlays page content from the left or right edge of the viewport. Used as a responsive navigation pattern: visible on mobile/tablet where a persistent SideNav is hidden. The drawer is controlled (open state managed by the host).

---

## 2. Data model

```typescript
interface NavDrawerItem {
  id: string
  label: string
  icon?: React.ReactNode
  href?: string
  onClick?: () => void
  badge?: string | number
  active?: boolean
}

interface NavDrawerSection {
  title?: string
  items: NavDrawerItem[]
}

interface NavDrawerProps {
  open: boolean
  onClose: () => void
  sections: NavDrawerSection[]
  header?: React.ReactNode
  footer?: React.ReactNode
  side?: 'left' | 'right'   // default: 'left'
  className?: string
}
```

---

## 3. Controlled open state

`open` is a required prop. The component does not manage open/close state internally. The host renders a toggle button (hamburger or close affordance) and passes `onClose` to receive close requests from: the overlay click, the close button inside the drawer, and Escape key.

---

## 4. Structure

```
NavDrawer
  Backdrop (fixed overlay, aria-hidden, onClick=onClose)  — visible only when open
  Drawer panel (role="dialog" aria-modal)
    Header row (header slot or default "Menu" label + close button)
    <nav> (scrollable, flex-1)
      Sections
        Section heading (optional)
        Items (rendered as <a> or <button>)
    Footer slot (optional)
```

---

## 5. Item rendering

Items with `href` render as `<a href="...">`. Items without `href` render as `<button type="button">`. Both call `item.onClick?.()` and then `onClose()` when clicked — the drawer closes automatically on item selection.

The `active` flag renders the item with a blue-tinted active style and `aria-current="page"`.

---

## 6. Side prop

| `side` | Translate-in from | Resting position |
|---|---|---|
| `'left'` (default) | Left edge (`-translate-x-full` when closed) | `left-0` |
| `'right'` | Right edge (`translate-x-full` when closed) | `right-0` |

Transition is `duration-300 ease-in-out`.

---

## 7. Header slot

When `header` is not provided, the default header renders `<span>Menu</span>`. The close button is always present regardless of whether a custom header is provided.

---

## 8. Related components

- **SideNav** — the persistent sidebar for wider viewports; NavDrawer is the overlay equivalent for narrow viewports.
- **AppBar** — typically contains the hamburger toggle that controls `open`.
