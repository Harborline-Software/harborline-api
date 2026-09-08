# FloatingActionButton — Interaction Contract

- **Component:** FloatingActionButton
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FloatingActionButton.Semantic.md) · [Accessibility](./FloatingActionButton.Accessibility.md) · [Styling](./FloatingActionButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/FloatingActionButton.tsx`
- **Catalog row:** #60 FloatingActionButton (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
ENABLED
  → click → onClick() fires
  → Spacebar / Enter → onClick() fires (native button behavior)

DISABLED (disabled=true)
  → click, keyboard → no-op (pointer-events-none + disabled HTML attr)
  → no opacity change on hover
```

No internal toggle or selection state. FAB is a stateless action trigger.

---

## 2. Click

`onClick` prop receives the native `React.MouseEvent<HTMLButtonElement>`. No internal state change — FAB is fully stateless beyond disabled.

---

## 3. Active feedback

`active:scale-95` — button scales down on pointer-down for haptic-style feedback.

---

## 4. Keyboard

| Key | Behaviour |
|---|---|
| `Enter` | Fires click (native `<button type="button">` behavior) |
| `Space` | Fires click (native) |
| `Tab` | Moves focus to next focusable element |

FAB participates in natural tab order. It is `fixed` in the viewport, not inside a scroll container — keyboard users can always reach it.

---

## 5. Known gaps

None.
