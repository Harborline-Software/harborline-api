# TextBox — Semantic Contract

- **Component:** TextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TextBox.Interaction.md) · [Accessibility](./TextBox.Accessibility.md) · [Styling](./TextBox.Styling.md)
- **Related contract:** [TextField.Semantic.md](./TextField.Semantic.md) — deprecated shim; TextField now delegates to TextBox.
- **Catalog row:** #134 TextField (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input>` via the `Input` primitive

---

## 1. Purpose

**TextBox** is the canonical single-line text input of `@harborline-software/ui-react` (CIC ruling 2026-06-12 — TextBox is the survivor name; TextField is a deprecated shim). Supports controlled and uncontrolled modes, optional clear button, optional password-reveal button, and a suffix-slot for icon or button composition.

---

## 2. Props

```typescript
interface TextBoxProps extends Omit<InputProps, 'onChange' | 'required' | 'disabled'> {
  value?: string
  defaultValue?: string
  onChange?: (value: string) => void   // string, not ChangeEvent
  placeholder?: string
  disabled?: boolean
  required?: boolean
  error?: boolean
  type?: string
  clearButton?: boolean
  showReveal?: boolean
  suffix?: React.ReactNode
  className?: string
}
```

---

## 3. Controlled / uncontrolled

When `value` is provided, the component is controlled. When omitted, the native input manages its own state. `defaultValue` seeds the initial value for uncontrolled usage.

---

## 4. onChange signature

`onChange` receives `string` (the new value), not a `ChangeEvent`. This matches the fleet's field-pair convention (§ family standard).

---

## 5. Error state

`error={true}` sets `aria-invalid` on the underlying input and applies the `border-destructive` visual treatment.

---

## 6. FormField context integration (Cohort-2, 2026-06-12)

TextBox reads `FormFieldContext` via `useFormField()`. When wrapped in a `<FormField>`, the following props are automatically applied:

| Context value | Activates when | Prop precedence |
|---|---|---|
| `describedBy` | FormField has `hint` or `error` | No prop override — context-only |
| `required` | FormField has `required={true}` | Prop `required` wins if explicitly set |
| `disabled` | FormField has `disabled={true}` | Prop `disabled` wins if explicitly set |

**Standalone (no FormField):** `useFormField()` returns an empty object; no change to existing standalone behaviour.

**Inside FormField:**
```tsx
<FormField name="email" label="Email" required>
  <TextBox name="email" type="email" />
</FormField>
```
TextBox automatically gains `aria-required`, `aria-invalid`, and `aria-describedby` wired to the FormField hint/error elements.

**Prop-overrides-context rule:** `resolved = prop ?? contextValue ?? false`.

**Replaces:** TextField (deprecated shim). See [TextField.Semantic.md](./TextField.Semantic.md) for migration guidance.
