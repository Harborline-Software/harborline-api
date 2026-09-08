# Rating — Semantic Contract

- **Component:** Rating
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Rating.Interaction.md) · [Accessibility](./Rating.Accessibility.md) · [Styling](./Rating.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Rating.tsx`
- **Catalog row:** #110 Rating (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled star rating control

---

## 1. Component purpose

**Rating** — a star-based rating input with hover preview. Supports integer and half-star (`precision=0.5`) values, custom icons, readonly display mode, and 3 sizes. Stars are rendered as individual `<button>` elements within a `role="radiogroup"` or `role="img"` container.

---

## 2. Props

```typescript
interface RatingProps {
  value?: number | null       // controlled; null = no rating
  defaultValue?: number       // uncontrolled seed
  onValueChange?: (value: number) => void
  max?: number                // number of stars; default: 5
  precision?: number          // 1 = whole stars; 0.5 = half stars; default: 1
  disabled?: boolean          // default: false
  readonly?: boolean          // default: false; display only, no interaction
  icon?: React.ReactNode      // filled star override
  emptyIcon?: React.ReactNode // empty star override
  size?: 'small' | 'medium' | 'large'  // default: 'medium'
  className?: string
}
```

---

## 3. Display value

```
display = hover ?? current ?? 0
```

While hovering, `hover` previews the would-be rating. On mouse-leave, `hover` clears and `current` (controlled or internal) is shown.

---

## 4. Precision

| `precision` | Behaviour |
|---|---|
| `1` | Click anywhere on a star → integer value `i` |
| `0.5` | Click left half → `i - 0.5`; click right half → `i` |

Half-star detection uses `e.clientX - rect.left < rect.width / 2`.

---

## 5. Modes

| Mode | Behaviour |
|---|---|
| Interactive (`!disabled && !readonly`) | Mouse hover previews; click sets value |
| Readonly | No hover, no click; `role="img"` on container |
| Disabled | No hover, no click; `disabled` on each button |
