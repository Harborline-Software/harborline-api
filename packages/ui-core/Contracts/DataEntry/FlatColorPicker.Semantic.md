# FlatColorPicker — Semantic Contract

- **Component:** FlatColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FlatColorPicker.Interaction.md) · [Accessibility](./FlatColorPicker.Accessibility.md) · [Styling](./FlatColorPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FlatColorPicker.tsx`
- **Catalog row:** #62 FlatColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled canvas-based color picker

---

## 1. Component purpose

**FlatColorPicker** — an inline (always-visible) color picker panel combining ColorGradient and ColorPalette sub-views. Manages a `pending` value separate from `value` — the user can interact without committing until the Apply button is clicked. Can also be used without actions (auto-commit mode) when `showActions=false`.

---

## 2. Props

```typescript
type ColorPickerView = 'gradient' | 'palette'

interface FlatColorPickerProps {
  value?: string                  // controlled committed color
  defaultValue?: string           // default: '#5470c6'
  onValueChange?: (color: string) => void
  views?: ColorPickerView[]       // default: ['gradient', 'palette']
  format?: 'hex' | 'rgb' | 'hsl' // reserved; M1 always hex
  opacity?: boolean               // default: true; passed to ColorGradient
  showActions?: boolean           // default: true; shows Apply/Cancel buttons
  disabled?: boolean              // default: false
  className?: string
}
```

---

## 3. Pending vs committed value model

When `showActions=true`:
- `pending` = color being previewed (updates as user interacts with sub-views)
- `current` = committed color (set only on Apply)
- Cancel: resets `pending` to `current`

When `showActions=false` (used by ColorPicker dropdown):
- Each sub-view selection immediately calls `onValueChange` and closes the parent dropdown

---

## 4. View tabs

When `views.length > 1`, a tab bar switches between `'gradient'` and `'palette'` views. Only one view is shown at a time. First view in the array is default active.
