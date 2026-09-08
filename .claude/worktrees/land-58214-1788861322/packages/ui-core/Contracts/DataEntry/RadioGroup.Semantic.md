# RadioGroup — Semantic Contract

- **Component:** RadioGroup
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./RadioGroup.Interaction.md) · [Accessibility](./RadioGroup.Accessibility.md) · [Styling](./RadioGroup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RadioGroup.tsx`
- **Catalog row:** #105 RadioGroup (`app-priority: high`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input type="radio">` (hand-rolled group)

---

## 1. Component purpose

**RadioGroup** — a mutually exclusive option selector using native `<input type="radio">` elements grouped by `name`. Supports vertical and horizontal orientation, disabled items, and error state.

---

## 2. Data model

```typescript
interface RadioOption {
  value: string
  label: string
  description?: string
  disabled?: boolean
}
```

---

## 3. Props

```typescript
interface RadioGroupProps {
  name: string            // groups all radios; used as field name in FormData
  value: string           // controlled — selected option value
  onChange: (v: string) => void
  options: RadioOption[]
  orientation?: 'vertical' | 'horizontal'  // default: 'vertical'
  disabled?: boolean                        // disables all options
  error?: boolean
}
```

Always fully controlled — no `defaultValue`.

---

## 4. FormField context

`useFormField()` provides `describedBy` for `aria-describedby` on the radiogroup container.
