# MegaMenu — Semantic Contract

- **Component:** MegaMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MegaMenu.Interaction.md) · [Accessibility](./MegaMenu.Accessibility.md) · [Styling](./MegaMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/MegaMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled mega-menu overlay

---

## 1. Component purpose

**MegaMenu** — a horizontal top-level navigation bar where each trigger button opens a large multi-column panel of links below it. One panel is open at a time. Used as the primary site/application navigation on wide-viewport layouts where a flat menu lacks the content capacity.

---

## 2. Data model

```typescript
interface MegaMenuLink {
  label: string
  href?: string
  onClick?: () => void
  description?: string
  icon?: React.ReactNode
  badge?: string
}

interface MegaMenuColumn {
  title?: string
  links: MegaMenuLink[]
}

interface MegaMenuTrigger {
  id: string
  label: string
  columns: MegaMenuColumn[]
  footer?: React.ReactNode
}

interface MegaMenuProps {
  triggers: MegaMenuTrigger[]
  className?: string
}
```

---

## 3. Structure

```
MegaMenu
  <nav role="menubar">
    <button role="menuitem" aria-haspopup aria-expanded> (one per trigger)
  Panel (role="menu") — conditionally rendered for the active trigger
    Columns (1–4)
      Column heading (optional)
      Links (role="menuitem" each, rendered as <a> or <button>)
    Footer (optional ReactNode)
```

---

## 4. Panel layout

The panel renders up to 4 columns using a CSS grid (`grid-cols-N` where N = `min(columns.length, 4)`). Columns beyond 4 are not separately handled — the grid class uses a dynamic Tailwind class string (`grid-cols-${N}`), which requires the class to be present in the Tailwind safelist for N > 4 to render correctly.

---

## 5. Link rendering

Each `MegaMenuLink` renders as `<a href>` when `href` is provided, otherwise as `<button type="button">`. Both carry `role="menuitem"`. Clicking any link fires `link.onClick?.()` and closes the active panel by setting `active` to `null`.

---

## 6. Footer slot

Each `MegaMenuTrigger` may include a `footer: React.ReactNode` rendered in a `bg-gray-50` strip at the bottom of the panel, separated by a top border. Useful for "View all" links or promotional content.

---

## 7. Open/close rules

- Exactly one trigger's panel is open at a time; clicking an already-open trigger closes it (toggle).
- Pressing Escape closes the active panel.
- Clicking outside the menu container closes the active panel.
- Navigating to a link closes the active panel.

---

## 8. Related components

- **Menu** — vertical single-level or nested menu; MegaMenu is the wide-viewport multi-column form.
- **NavigationMenu** — Radix-based navigation with hover triggers; MegaMenu is a fully custom click-first implementation.
- **AppBar** — the surface MegaMenu is most commonly placed within.
