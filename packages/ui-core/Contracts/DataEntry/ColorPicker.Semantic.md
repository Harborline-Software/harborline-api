# ColorPicker — Semantic Contract

- **Component:** ColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ColorPicker.Interaction.md) · [Accessibility](./ColorPicker.Accessibility.md) · [Styling](./ColorPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPicker.tsx`
- **Catalog row:** #31 ColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — composes FlatColorPicker (hand-rolled)

---

## 1. Component purpose

**ColorPicker** — a dropdown trigger button that opens a FlatColorPicker panel. The trigger displays the current color as a swatch + hex string. Supports gradient, palette, and opacity views. Returns colors as hex/rgb/hsl strings.

---

## 2. Props

```typescript
type ColorPickerView = 'gradient' | 'palette'  // from FlatColorPicker

interface ColorPickerProps {
  value?: string                    // controlled hex/rgb/hsl color string
  defaultValue?: string             // default: '#5470c6'
  onValueChange?: (color: string) => void
  views?: ColorPickerView[]         // default: ['gradient', 'palette']
  format?: 'hex' | 'rgb' | 'hsl'   // reserved; M1 always returns hex
  opacity?: boolean                 // default: true; shows opacity slider
  disabled?: boolean                // default: false
  size?: 'small' | 'medium' | 'large'         // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'     // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full'  // default: 'medium'
  placeholder?: string              // default: 'Pick a color'; also aria-label
  className?: string
}
```

---

## 3. Composition

ColorPicker = trigger button + FlatColorPicker dropdown. It delegates all color selection logic to FlatColorPicker (with `showActions={false}` so the panel auto-closes on selection).

---

## 4. Value format

In M1, color values are always hex strings regardless of the `format` prop (reserved for future implementation).

---

## 5. Companion components

| Catalog entry | Relation |
|---|---|
| #28 ColorGradient | View within FlatColorPicker |
| #30 ColorPalette | View within FlatColorPicker |
| #60 FlatColorPicker | Inline version (no trigger button) |
