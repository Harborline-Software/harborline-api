# Carousel — Semantic Contract

- **Component:** Carousel
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Carousel.Interaction.md) · [Accessibility](./Carousel.Accessibility.md) · [Styling](./Carousel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Carousel.tsx`
- **Catalog row:** #22 Carousel (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled slide container

---

## 1. Component purpose

**Carousel** — a slide container that reveals one or more children at a time. Supports auto-play, infinite wrap-around, arrow navigation buttons, dot indicator navigation, and both horizontal and vertical orientations.

---

## 2. Props

```typescript
interface CarouselProps {
  children: React.ReactNode           // slides; any React children
  autoPlay?: boolean                  // default: false
  autoPlayInterval?: number           // ms; default: 3000
  infinite?: boolean                  // default: true; wraps at boundaries
  showArrows?: boolean                // default: true; shown when count > slidesPerView
  showDots?: boolean                  // default: true; shown when count > 1
  orientation?: 'horizontal' | 'vertical'  // default: 'horizontal'
  slidesPerView?: number              // default: 1
  gap?: number                        // px gap between slides; default: 0
  value?: number                      // controlled active slide index
  defaultValue?: number               // default: 0
  onValueChange?: (index: number) => void
  className?: string
}
```

---

## 3. Slide model

Children are collected via `React.Children.toArray(children)`. Each child becomes a slide. The active index is zero-based.

---

## 4. Controlled vs uncontrolled

`value` prop = controlled; `defaultValue` = uncontrolled seed (default 0). When controlled, `onValueChange` must update `value` externally for navigation to work.

---

## 5. Infinite wrap-around

When `infinite=true`: navigating before the first slide wraps to the last; after the last wraps to the first. Implemented via modular arithmetic `((n % count) + count) % count`.

When `infinite=false`: navigation clamps at 0 and `count - slidesPerView`.

---

## 6. Multi-slide view

`slidesPerView > 1` shows multiple slides simultaneously. Arrow and dot display conditions account for this:
- Arrows shown when `count > slidesPerView`
- Arrows disabled at boundaries when `infinite=false`
