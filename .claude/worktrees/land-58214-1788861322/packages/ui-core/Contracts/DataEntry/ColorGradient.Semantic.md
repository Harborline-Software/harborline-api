# ColorGradient — Semantic Contract

- **Component:** ColorGradient
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ColorGradient.Interaction.md) · [Accessibility](./ColorGradient.Accessibility.md) · [Styling](./ColorGradient.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorGradient.tsx`
- **Catalog row:** #29 ColorGradient (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled canvas gradient picker

---

## 1. Component purpose

**ColorGradient** — an HSV gradient color picker with: (1) a saturation/brightness gradient canvas, (2) a hue range slider, (3) an optional opacity range slider, (4) a hex text input. Used as a sub-view within FlatColorPicker/ColorPicker or standalone.

---

## 2. Props

```typescript
interface ColorGradientProps {
  value?: string            // controlled hex color string
  defaultValue?: string     // default: '#5470c6'
  onValueChange?: (color: string) => void
  format?: 'hex' | 'rgb' | 'hsl'  // reserved; M1 always returns hex
  opacity?: boolean         // default: true; shows opacity slider
  disabled?: boolean        // default: false
  className?: string
}
```

---

## 3. Internal model

Uses HSV (hue 0-360, sat 0-1, brightness 0-1) + alpha (0-1) internally. Converts:
- Input hex → HSV via `hexToHsv`
- User interaction → `hsvToHex` → emits hex
- Hex text input → attempts `hexToHsv` parse; falls back silently on invalid input

---

## 4. Gradient canvas

A 192px wide × 128px tall div with a CSS gradient background. The gradient renders:
- Horizontal: white (left) → pure hue color (right)  
- Vertical overlay: transparent (top) → black (bottom)

Clicking the canvas sets saturation (x-axis) and brightness (y-axis).

---

## 5. Sliders

| Slider | Range | Aria label |
|---|---|---|
| Hue | 0–360 | `"Hue"` |
| Opacity | 0–100 | `"Opacity"` |

---

## 6. Hex text input

Accepts raw hex string entry. Parses via `hexToHsv` and updates the gradient state.
