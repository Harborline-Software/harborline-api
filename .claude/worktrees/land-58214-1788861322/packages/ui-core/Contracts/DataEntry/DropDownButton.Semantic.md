# DropDownButton — Semantic Contract

- **Component:** DropDownButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DropDownButton.Interaction.md) · [Accessibility](./DropDownButton.Accessibility.md) · [Styling](./DropDownButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DropDownButton.tsx`
- **Catalog row:** #47 DropDownButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled split button with dropdown

---

## 1. Component purpose

**DropDownButton** — a themed action button that opens a dropdown menu of items when clicked. Supports icon, multiple fill modes, and left/right popup alignment.

---

## 2. Props

```typescript
interface DropDownButtonItem {
  text: string
  icon?: React.ReactNode
  disabled?: boolean
  onClick?: () => void
}

type DropDownButtonTheme = 'primary' | 'secondary' | 'base' | 'tertiary' | 'info' | 'success' | 'warning' | 'error'
type DropDownButtonFillMode = 'solid' | 'flat' | 'outline' | 'link' | 'clear'
type DropDownButtonSize = 'small' | 'medium' | 'large'

interface DropDownButtonProps {
  text: string                             // required; label on trigger button
  icon?: React.ReactNode                   // optional leading icon
  items: DropDownButtonItem[]              // required; menu items
  themeColor?: DropDownButtonTheme         // default: 'base'
  fillMode?: DropDownButtonFillMode        // default: 'solid'
  size?: DropDownButtonSize                // default: 'medium'
  disabled?: boolean                       // default: false
  popupAlign?: 'left' | 'right'           // default: 'left'; which edge dropdown aligns to
  className?: string
}
```

---

## 3. Item model

Each item has a `text` label, optional `icon`, optional `disabled` flag, and optional `onClick` callback. Item `onClick` fires when the item is clicked; the menu closes after any item click.

---

## 4. fillMode interaction with themeColor

When `fillMode='solid'`, the button is rendered with full `themeColor` background styling. When `fillMode` is any other value (`flat`, `outline`, `link`, `clear`), the button uses a non-solid surface and `themeColor` does not apply a background.

---

## 5. popupAlign

Controls which edge of the dropdown aligns to the trigger:
- `'left'` — dropdown left edge aligns to trigger left edge (default)
- `'right'` — dropdown right edge aligns to trigger right edge
