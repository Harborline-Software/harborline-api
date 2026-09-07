# Rating — Interaction Contract

- **Component:** Rating
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Rating.Semantic.md) · [Accessibility](./Rating.Accessibility.md) · [Styling](./Rating.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Rating.tsx`
- **Catalog row:** #110 Rating (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine (interactive mode)

```
IDLE
  → mouseMove over star i → HOVER(v) where v = getHoverValue(i, e)
  → click star i → SET(v); IDLE

HOVER(v)
  → mouseMove over different star j → HOVER(v')
  → mouseLeave → IDLE (hover clears, display = current)
  → click → SET(v); IDLE

SET(v)
  → if uncontrolled: setInternal(v)
  → onValueChange?.(v)
```

---

## 2. Hover preview

`onMouseMove` on each star button: calls `getHoverValue(i, e)` to compute preview value, sets `hover` state. Stars visually update to show the preview rating.

`onMouseLeave` on each star button: clears `hover`. Display reverts to `current`.

---

## 3. Click

`onClick` on each star: computes value via `getHoverValue`, emits `onValueChange(v)`, updates internal state if uncontrolled.

---

## 4. Keyboard

| Key | Behaviour |
|---|---|
| `Tab` | Moves focus to each star button in order |
| `Enter` / `Space` | Activates star (sets value at that integer position) |

Half-star precision is **mouse-only** in M1 — keyboard activation always sets integer value at the star's index. Gap G-RAT2.

---

## 5. Readonly / disabled

When `readonly=true` or `disabled=true`: no `onMouseMove`, `onMouseLeave`, or `onClick` handlers attached. Disabled buttons have the native `disabled` attribute.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RAT1 | Low | No way to clear (set to 0) via keyboard in M1 | Accepted-risk M1; value can only increase or stay |
| G-RAT2 | Low | Half-star selection requires mouse hover; keyboard always sets integer values | Accepted-risk M1 |
