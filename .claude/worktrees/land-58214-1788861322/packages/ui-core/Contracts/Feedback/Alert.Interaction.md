# Alert — Interaction Contract

- **Component:** Alert
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Alert.Semantic.md) · [Accessibility](./Alert.Accessibility.md) · [Styling](./Alert.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Alert.tsx`
- **Catalog rows:** #153 Alert / #78 CalloutBox (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Dismiss action

| Condition | Element | Effect |
|---|---|---|
| `closable=true` | Close `<button>` click | `onClose?.()` |
| `closable=false` (default) | — | No close button rendered |

The Alert itself does not manage visibility — the parent controls mount/unmount. `onClose` is a signal to the parent to unmount or hide the Alert.

---

## 2. Action slot

`action` prop renders arbitrary content (typically a `<Button>` or link) below the body text. The Alert has no built-in action semantics — all interaction in the action slot is provided by the caller.

---

## 3. No auto-dismiss

Alert does not auto-dismiss. It renders until the parent unmounts it.

---

## 4. Known gaps

None. Interaction surface is intentionally minimal — the Alert is a static information display with an optional dismiss.
