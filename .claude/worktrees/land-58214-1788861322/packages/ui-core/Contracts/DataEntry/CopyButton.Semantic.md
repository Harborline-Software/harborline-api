# CopyButton — Semantic Contract

- **Component:** CopyButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CopyButton.Interaction.md) · [Accessibility](./CopyButton.Accessibility.md) · [Styling](./CopyButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/CopyButton.tsx`
- **Catalog row:** #A27 CopyButton (`app-priority: low`, `library-scope: v1`) — Harborline-native clipboard copy utility button
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<button>` with clipboard API

---

## 1. Component purpose

**CopyButton** — a button that copies a string to the clipboard via `navigator.clipboard.writeText`. Provides transient visual and accessible feedback after a successful copy. Self-contained: no external state required.

---

## 2. Props

```typescript
interface CopyButtonProps {
  value: string            // text to copy to clipboard
  label?: string           // button label in idle state; default: 'Copy'
  successLabel?: string    // button label after copy; default: 'Copied!'
  successDuration?: number // ms to show success state; default: 1500
  className?: string
}
```

---

## 3. Internal state

`copied: boolean` — `false` initially; set to `true` on successful clipboard write; reset to `false` after `successDuration` milliseconds via `setTimeout`.

---

## 4. Clipboard API

Uses `navigator.clipboard.writeText(value)`. Errors are caught and swallowed silently — no error state is exposed (G-CPBTN1).

---

## 5. Label rendering

Renders both label and icon. Label text switches between `label` and `successLabel` based on `copied` state.
