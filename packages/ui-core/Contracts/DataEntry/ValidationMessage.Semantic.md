# ValidationMessage — Semantic Contract

- **Component:** ValidationMessage
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ValidationMessage.Interaction.md) · [Accessibility](./ValidationMessage.Accessibility.md) · [Styling](./ValidationMessage.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationMessage.tsx`
- **Catalog row:** #145 ValidationMessage (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<span>` wrapper

---

## 1. Component purpose

**ValidationMessage** — renders a single validation error message as an alert paragraph. Returns `null` when `message` is empty. Used standalone or inside FormField's error slot.

---

## 2. Props

```typescript
interface ValidationMessageProps {
  for?: string      // input id — used to generate id="${for}-error" for aria-describedby chaining
  message?: string  // error text; null/undefined → null render
  className?: string
}
```

---

## 3. Render conditions

- `message` is falsy → returns `null`
- `message` is truthy → renders `<p role="alert" id="${for}-error">` with message text

---

## 4. id convention

When `for` is provided, `id="${for}-error"`. Callers can then set `aria-describedby="${for}-error"` on the associated input.

---

## 5. Non-interactive

ValidationMessage has no state and no event handlers. It is a pure display component.
