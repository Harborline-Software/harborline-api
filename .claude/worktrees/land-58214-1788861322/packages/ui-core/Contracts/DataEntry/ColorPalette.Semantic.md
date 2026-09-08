# ColorPalette — Semantic Contract

- **Component:** ColorPalette
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ColorPalette.Interaction.md) · [Accessibility](./ColorPalette.Accessibility.md) · [Styling](./ColorPalette.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPalette.tsx`
- **Catalog row:** #30 ColorPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled color swatch grid

---

## 1. Component purpose

**ColorPalette** — a grid of color swatches for discrete color selection. Each swatch is a `role="radio"` button. Used as a sub-view within FlatColorPicker/ColorPicker or standalone.

---

## 2. Props

```typescript
interface ColorPaletteProps {
  value?: string              // controlled; hex color string
  defaultValue?: string
  onValueChange?: (color: string) => void
  palette?: string[]          // default: 24-swatch palette (grays + primaries + pastels)
  tileSize?: number           // px per swatch; default: 24
  columns?: number            // grid columns; default: 10
  disabled?: boolean          // default: false
  className?: string
}
```

---

## 3. Default palette

24 swatches: 8 grays + 8 primary/saturated + 8 pastel variations.

---

## 4. Selection model

Single-select — one swatch can be checked at a time. `value` is the hex string of the selected swatch. `null` = no selection (default/uncontrolled seed).
