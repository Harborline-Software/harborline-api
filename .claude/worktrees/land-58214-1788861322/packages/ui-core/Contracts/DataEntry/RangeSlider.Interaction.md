# RangeSlider — Interaction Contract

- **Component:** RangeSlider
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RangeSlider.Semantic.md) · [Accessibility](./RangeSlider.Accessibility.md) · [Styling](./RangeSlider.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RangeSlider.tsx`
- **Catalog row:** #109 RangeSlider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Dual-handle interaction

Two `<input type="range">` elements are stacked via `absolute inset-0`. Both are `pointer-events-none` with `pointer-events-auto` on their `::-webkit-slider-thumb` / `::-moz-range-thumb` pseudo-elements. This allows each thumb to be dragged independently while the track itself is not interactive.

---

## 2. Low handle

```
onChange: v = Math.min(inputValue, high - step)
→ onValueChange([v, high])
```

Low handle cannot exceed `high - step`.

---

## 3. High handle

```
onChange: v = Math.max(inputValue, low + step)
→ onValueChange([low, v])
```

High handle cannot drop below `low + step`.

---

## 4. Keyboard (per native `<input type="range">`)

| Key | Behaviour |
|---|---|
| `Tab` | Moves focus between low and high thumbs |
| `Arrow Right` / `Arrow Up` | Increment focused thumb by `step` |
| `Arrow Left` / `Arrow Down` | Decrement focused thumb by `step` |
| `Home` | Jump focused thumb to `min` (low) or `low + step` (high) |
| `End` | Jump focused thumb to `high - step` (low) or `max` (high) |

Clamping still applies during keyboard navigation.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RS1 | Low | Thumb overlap: when `low` and `high` are adjacent, thumbs overlap visually; z-index between them is not managed | Accepted-risk M1 |
