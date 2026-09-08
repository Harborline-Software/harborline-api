# Slider — Semantic Contract

- **Component:** Slider
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Slider.Interaction.md) · [Accessibility](./Slider.Accessibility.md) · [Styling](./Slider.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Slider.tsx`
- **Catalog row:** #119 Slider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<input type="range">` styled component

---

## 1. Component purpose

**Slider** — a range input control with a custom visual track and thumb. Supports min/max/step, controlled and uncontrolled value, horizontal orientation, optional tick marks, and 3 sizes. Implemented as a native `<input type="range">` overlaid with CSS visual layers.

---

## 2. Props

```typescript
interface SliderProps {
  value?: number              // controlled value
  defaultValue?: number       // default: 0
  onValueChange?: (value: number) => void
  min?: number                // default: 0
  max?: number                // default: 100
  step?: number               // default: 1
  disabled?: boolean          // default: false
  orientation?: 'horizontal' | 'vertical'  // default: 'horizontal'
  showTickMarks?: boolean     // default: false
  tickStep?: number           // tick interval; default: same as step
  size?: 'small' | 'medium' | 'large'  // default: 'medium'
  className?: string
}
```

---

## 3. Controlled vs uncontrolled

`value` prop = controlled; `defaultValue` = uncontrolled seed (default 0). Controlled and uncontrolled follow standard React patterns.

---

## 4. Visual layers

The custom track and thumb are purely visual overlays; the native `<input type="range">` is rendered `opacity-0` and sits on top to capture all interaction. This means keyboard, pointer, and AT behavior is delegated to the native element.

---

## 5. Tick marks

When `showTickMarks=true`, ticks are rendered as `<span>` elements below the track. The tick interval is `tickStep ?? step`. Ticks are generated for `t = min; t <= max; t += tickStep`.

---

## 6. Fill indicator

The filled portion of the track is a `div` with `width: ${pct}%` where `pct = (value - min) / (max - min) * 100`.
