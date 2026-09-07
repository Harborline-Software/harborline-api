# Carousel — Interaction Contract

- **Component:** Carousel
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Carousel.Semantic.md) · [Accessibility](./Carousel.Accessibility.md) · [Styling](./Carousel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Carousel.tsx`
- **Catalog row:** #22 Carousel (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Navigation

### Arrow buttons

| Button | Behaviour | Disabled condition |
|---|---|---|
| Previous | `go(index - 1)` | `!infinite && index === 0` |
| Next | `go(index + 1)` | `!infinite && index >= count - slidesPerView` |

Arrows are only rendered when `showArrows=true` AND `count > slidesPerView`.

### Dot buttons

Clicking dot `i` calls `go(i)`. Dots rendered when `showDots=true` AND `count > 1`.

---

## 2. `go(n)` logic

```
if infinite: next = ((n % count) + count) % count
else: next = clamp(n, 0, count - 1)
```

Emits `onValueChange(next)`. If uncontrolled, updates internal state.

---

## 3. Auto-play

When `autoPlay=true`: a `setInterval` fires `go(index + 1)` every `autoPlayInterval` ms. Cleared on unmount.

**⚠ Interval thrashing.** If `go` is a non-memoized function, the `useEffect` dependency on `go` causes the interval to clear and re-arm on every render. Actual cadence becomes `autoPlayInterval + N×frame_time`. **Required fix:** memoize `go` with `useCallback` and depend only on `[autoPlayInterval, isPaused]`, or use a ref-based pattern:

```typescript
// Ref pattern — immune to go() identity churn:
const goRef = useRef(go)
useEffect(() => { goRef.current = go }, [go])
useEffect(() => {
  if (!autoPlay || isPaused) return
  const id = setInterval(() => goRef.current(index + 1), autoPlayInterval)
  return () => clearInterval(id)
}, [autoPlay, autoPlayInterval, isPaused]) // NOT [go]
```

Auto-play does not pause on user hover/focus in M1.

---

## 4. Keyboard

| Key | Behaviour |
|---|---|
| `Tab` | Cycles through: Previous arrow → Dot buttons → Next arrow |
| `Enter` / `Space` | Activates focused arrow or dot |
| `Arrow Left` / `Arrow Right` | No built-in handler (M1) |

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CAR1 | Medium | Auto-play does not pause on hover or keyboard focus — fails WCAG 2.1 SC 2.2.2 (Pause, Stop, Hide) | Accepted-risk M1; host can disable autoPlay for accessible contexts |
| G-CAR2 | Low | No swipe/drag gesture support in M1 | Accepted-risk M1; touch users rely on arrow buttons |
| G-CAR3 | Low | No arrow key navigation between slides; keyboard users must Tab to arrows | Accepted-risk M1 |
