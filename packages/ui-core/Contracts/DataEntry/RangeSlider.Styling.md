# RangeSlider — Styling Contract

- **Component:** RangeSlider
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RangeSlider.Semantic.md) · [Interaction](./RangeSlider.Interaction.md) · [Accessibility](./RangeSlider.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RangeSlider.tsx`
- **Catalog row:** #109 RangeSlider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`w-full`

---

## 2. Track area

Container: `relative h-5` (fixed height accommodates thumbs).

| Layer | Classes |
|---|---|
| Background track | `absolute top-1/2 h-1.5 w-full -translate-y-1/2 rounded-full bg-gray-200` |
| Active fill | `absolute top-1/2 h-1.5 -translate-y-1/2 rounded-full bg-blue-500`; inline `left: ${lowPct}%`, `width: ${highPct - lowPct}%` |

*Note: Uses hardcoded `bg-blue-500` and `bg-gray-200` — not themed to the Harborline token palette. This is an M1 inconsistency.*

---

## 3. Range inputs (overlay)

Both inputs: `pointer-events-none absolute inset-0 h-full w-full appearance-none bg-transparent disabled:opacity-50`

Thumb styles (CSS pseudo-elements):
```
[&::-webkit-slider-thumb]:pointer-events-auto
[&::-webkit-slider-thumb]:h-4 [&::-webkit-slider-thumb]:w-4
[&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:rounded-full
[&::-webkit-slider-thumb]:border-2 [&::-webkit-slider-thumb]:border-blue-500
[&::-webkit-slider-thumb]:bg-white [&::-webkit-slider-thumb]:shadow-sm
[&::-webkit-slider-thumb]:transition-shadow [&::-webkit-slider-thumb]:hover:shadow-md
[&::-moz-range-thumb]: equivalent classes
```

---

## 4. Value labels

Container: `mt-2 flex justify-between`
Each label: `text-xs text-gray-500`
