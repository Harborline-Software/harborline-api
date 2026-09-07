# RangeSlider — Semantic Contract

- **Component:** RangeSlider
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./RangeSlider.Interaction.md) · [Accessibility](./RangeSlider.Accessibility.md) · [Styling](./RangeSlider.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RangeSlider.tsx`
- **Catalog row:** #109 RangeSlider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled dual-thumb range slider

---

## 1. Component purpose

**RangeSlider** — a dual-handle range selector that captures a low/high value pair from a bounded numeric range. Implemented as two overlaid native `<input type="range">` elements sharing a common track, with a highlighted fill between the handles.

---

## 2. Props

```typescript
interface RangeSliderProps {
  min: number                           // required
  max: number                           // required
  step?: number                         // default: 1
  value: [number, number]               // controlled; [low, high]
  onValueChange: (value: [number, number]) => void
  disabled?: boolean                    // default: false
  formatValue?: (value: number) => string  // display formatter; default: String
  ariaLabel?: string                    // prefix for ARIA labels; default: 'Range'
  className?: string
}
```

---

## 3. Controlled-only

RangeSlider is **fully controlled** — `min`, `max`, `value`, and `onValueChange` are all required or have structural defaults. There is no `defaultValue`.

---

## 4. Value invariant

The value tuple `[low, high]` must satisfy `min ≤ low < high ≤ max`. The component enforces this via clamping:
- Low handle: `Math.min(newLow, high - step)` — cannot reach or exceed `high`
- High handle: `Math.max(newHigh, low + step)` — cannot drop to or below `low`

---

## 5. Display labels

Below the track, two `<span>` elements show `formatValue(low)` and `formatValue(high)`. Default formatter is `String` (numeric string).
