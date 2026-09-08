# Carousel — Styling Contract

- **Component:** Carousel
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Carousel.Semantic.md) · [Interaction](./Carousel.Interaction.md) · [Accessibility](./Carousel.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Carousel.tsx`
- **Catalog row:** #22 Carousel (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`relative overflow-hidden`

---

## 2. Slide track

| Orientation | Classes |
|---|---|
| `horizontal` | `flex flex-row transition-transform duration-300` |
| `vertical` | `flex flex-col transition-transform duration-300` |

Transform applied inline — **⚠ pixel-gap drift warning**:

`translateX(-${index * (100 / slidesPerView)}%)` computes position as a percentage of the track width. When `gap` (pixels) is applied to the flex container, each slide is narrower than `(100 / slidesPerView)%` of the track, so the percentage-based transform overshoots by `index * gap` pixels. Misalignment is visible and grows with each slide.

**Correct implementations (choose one):**
1. **Percentage with no gap**: Drop `gap`; use internal slide padding (`px-N`) for visual spacing instead.
2. **Pixel-based transform via ref**: Measure the container width with `useRef` + `offsetWidth` and compute `translateX(-(index * (containerWidth / slidesPerView + gap) + gap) + containerOffset)px` in pixels.
3. **CSS scroll-snap**: Use `overflow: hidden` + `scroll-snap-type: x mandatory` on the track; scroll-snap inherits gap correctly without a manual transform formula.

Gap applied via inline `style={{ gap }}` (pixels) — only compatible with approaches 2 or 3 above.

---

## 3. Individual slide

`shrink-0` + inline width/height computed from `slidesPerView` and `gap`.

---

## 4. Arrow buttons

```
absolute z-10 flex items-center justify-center h-8 w-8 rounded-full
bg-background/80 border border-border shadow-sm
hover:bg-background disabled:opacity-30
```

| Orientation | Previous position | Next position |
|---|---|---|
| `horizontal` | `left-2 top-1/2 -translate-y-1/2` | `right-2 top-1/2 -translate-y-1/2` |
| `vertical` | `top-2 left-1/2 -translate-x-1/2` | `bottom-2 left-1/2 -translate-x-1/2` |

Arrow label characters: `‹` / `›` (horizontal), `↑` / `↓` (vertical).

---

## 5. Dot indicators

Container:
| Orientation | Position classes |
|---|---|
| `horizontal` | `absolute bottom-2 left-1/2 -translate-x-1/2 flex flex-row gap-1.5` |
| `vertical` | `absolute right-2 top-1/2 -translate-y-1/2 flex flex-col gap-1.5` |

Each dot: `h-2 w-2 rounded-full transition-colors`

| State | Classes |
|---|---|
| Active (current index) | `bg-primary` |
| Inactive | `bg-border hover:bg-muted-foreground` |
