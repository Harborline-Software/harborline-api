# Tag — Interaction Contract

- **Component:** Tag
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tag.Semantic.md) · [Interaction](./Tag.Interaction.md) · [Accessibility](./Tag.Accessibility.md) · [Styling](./Tag.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Tag.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

Tag has **no internal state**. It is fully controlled.

```
onRemove absent OR disabled=true: non-interactive (presentational)
onRemove present AND disabled=false: remove button is active

User clicks remove → e.stopPropagation() → if !disabled → onRemove()
```

---

## 2. Remove button behaviour

| Condition | Remove button | Firing |
|---|---|---|
| `onRemove` absent | Not rendered | n/a |
| `onRemove` present, `disabled=false` | Rendered, enabled | `onRemove()` fires on click |
| `onRemove` present, `disabled=true` | Rendered, `disabled` HTML attribute | Click disabled; no `onRemove` call |

`e.stopPropagation()` is called unconditionally on remove button click, preventing bubbling even when `disabled`. This prevents parent list-item click handlers from firing.

---

## 3. `disabled` behaviour

When `disabled={true}`:

- The remove `<button>` element has `disabled` HTML attribute — native browser disabling prevents keyboard and mouse activation.
- The remove button's cursor is `cursor-not-allowed`.
- The tag's opacity is `opacity-60`.

---

## 4. Keyboard behaviour

| Key | Element | Action |
|---|---|---|
| Tab | Remove button (when present and not disabled) | Focuses button |
| Enter / Space | Remove button | Fires `onRemove()` (native `<button>` behaviour) |
| Delete / Backspace | Remove button | **Also fires `onRemove()`** — required for accessibility |

The tag body (`<span>`) is not focusable. Only the remove button enters the tab order.

### 4.1 Delete / Backspace requirement

When focus is on the remove button, `Delete` and `Backspace` MUST also fire `onRemove()`. This is required for AT users who navigate to the button with Tab and expect to press Delete to remove — it mirrors the keyboard pattern used by chip/pill inputs across the web (e.g., Google, GitHub, Select component chips). Without Delete/Backspace support, keyboard users must discover and use Enter/Space instead, which is a non-standard pattern for "removal" affordances.

Implementation:
```tsx
<button
  onKeyDown={(e) => {
    if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault()
      if (!disabled) onRemove()
    }
  }}
  onClick={(e) => { e.stopPropagation(); if (!disabled) onRemove() }}
  ...
/>
```

`e.preventDefault()` on Delete prevents the browser from navigating back (Backspace) or triggering other browser-level delete actions.

---

## 5. `stopPropagation` note

The `e.stopPropagation()` call means that if a Tag is rendered inside a clickable container (e.g., a list row with `onClick`), clicking the remove button will NOT trigger the container's click. This is intentional — remove is a distinct action from row selection.

---

## 6. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No `onRemove` confirmation or undo | Tags are removed immediately; host implements undo if needed |
| I2 | No click interaction on tag body | Tag body carries no `onClick`; hosts use `...props` passthrough or wrapping |
| I3 | Remove fires immediately on mouse-up | No long-press delay or secondary confirmation |
| I4 | Delete/Backspace keyboard handler | [RESOLVED 2026-06-06] §4.1: Delete/Backspace now spec'd on remove button |
