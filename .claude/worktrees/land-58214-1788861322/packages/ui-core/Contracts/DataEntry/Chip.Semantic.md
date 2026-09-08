# Chip — Semantic Contract

- **Component:** Chip
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Chip.Interaction.md) · [Accessibility](./Chip.Accessibility.md) · [Styling](./Chip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Chip.tsx`
- **Catalog rows:** #25 Chip (`app-priority: low`, `library-scope: planned`) · #26 ChipList (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled chip `<button>/<span>`

---

## 1. Component purpose

**Chip** — a single togglable badge/tag button. Supports selection state, remove button, avatar, and icon slots.

**ChipList** — a container that renders a collection of Chips with unified selection logic (none / single / multiple).

---

## 2. Props

### Chip

```typescript
type ChipFillMode = 'solid' | 'flat' | 'outline'
type ChipSize = 'small' | 'medium' | 'large'
type ChipThemeColor =
  | 'base' | 'primary' | 'secondary' | 'tertiary'
  | 'info' | 'success' | 'warning' | 'error'

interface ChipProps {
  text?: string
  icon?: ReactNode
  avatar?: string              // URL for avatar image
  removable?: boolean          // default: false; shows × remove button
  selected?: boolean           // controlled selected state
  defaultSelected?: boolean    // default: false
  onSelectedChange?: (selected: boolean) => void
  onRemove?: () => void
  disabled?: boolean
  fillMode?: ChipFillMode      // default: 'solid'
  themeColor?: ChipThemeColor  // default: 'base'
  size?: ChipSize              // default: 'medium'
  rounded?: 'small' | 'medium' | 'large' | 'full'  // default: 'full'
  className?: string
}
```

### ChipList

```typescript
interface ChipListItem {
  text: string
  value: string | number
  removable?: boolean
  disabled?: boolean
}

interface ChipListProps {
  chips?: ChipListItem[]
  selection?: 'none' | 'single' | 'multiple'   // default: 'none'
  value?: Array<string | number>                 // controlled selected values
  defaultValue?: Array<string | number>
  onValueChange?: (values: Array<string | number>) => void
  onRemove?: (value: string | number) => void
  fillMode?: ChipFillMode
  size?: ChipSize
  className?: string
}
```

---

## 3. Data model

`Chip.selected` is a boolean toggle. No concept of a "value" on Chip directly — the value semantic is on `ChipList`.

`ChipList.value` is an array of selected `chip.value` items. Single selection enforces at most 1 item in the array.

---

## 4. Size scale

| Size | Height / padding |
|---|---|
| `small` | `h-6 px-2 text-xs gap-1` |
| `medium` | `h-8 px-3 text-sm gap-1.5` |
| `large` | `h-10 px-4 text-base gap-2` |
