# CheckBox — Semantic Contract

- **Component:** CheckBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CheckBox.Interaction.md) · [Accessibility](./CheckBox.Accessibility.md) · [Styling](./CheckBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CheckBox.tsx`
- **Catalog row:** #24 Checkbox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input type="checkbox">`

---

## 1. Component purpose

**CheckBox** — a standalone checkbox input supporting checked, unchecked, and indeterminate states with optional label. Lower-level standalone primitive; for a form-integrated checkbox with bundled label + hint + error layout, use CheckboxField, which is an independent component built directly on Radix Checkbox (CheckboxField does NOT compose CheckBox — the two are parallel implementations).

---

## 2. Props

```typescript
interface CheckBoxProps {
  checked?: boolean | 'indeterminate'   // 'indeterminate' is input-only; see §4
  defaultChecked?: boolean
  onChange?: (checked: boolean) => void // always boolean, never 'indeterminate'; see §5
  label?: string
  labelPlacement?: 'before' | 'after'  // default: 'after'
  disabled?: boolean                    // default: false
  required?: boolean
  id?: string
  name?: string
  value?: string
  size?: 'small' | 'medium' | 'large'  // default: 'medium'
  className?: string
}
```

---

## 3. Controlled / uncontrolled

When `checked` is provided, the component is controlled. When omitted, the native input manages its own state (`defaultChecked` seeds the initial value).

---

## 4. Indeterminate state

When `checked === 'indeterminate'`:
- The native input `checked` prop is set to `false` (to avoid conflict)
- `inputRef.current.indeterminate = true` is set via `useEffect`
- `onChange` fires `true` or `false` (the result of the native checkbox change) — the indeterminate state is NOT re-fired on user interaction; it resets to checked/unchecked

---

## 5. onChange signature

`onChange` receives `boolean` (never `'indeterminate'`) because it is derived from `e.target.checked` on the native change event. The `'indeterminate'` value is an input-only state.

---

## 6. Label rendering

- No `label` → renders `<span className={className}>{checkbox}</span>`
- With `label` → renders `<label>` wrapping checkbox + label text
- `labelPlacement='before'`: label text appears before the checkbox
- `labelPlacement='after'` (default): label text appears after the checkbox

---

## 7. FormField context integration (Cohort-2, 2026-06-12)

CheckBox reads `FormFieldContext` via `useFormField()`. When wrapped in a `<FormField>`, the following props are automatically applied:

| Context value | Activates when | Prop precedence |
|---|---|---|
| `describedBy` | FormField has `hint` or `error` | No prop override — context-only |
| `required` | FormField has `required={true}` | Native `required` attr on the `<input>` |
| `disabled` | FormField has `disabled={true}` | Prop `disabled` wins if explicitly set |

**`error` prop (Cohort-2):** CheckBox now accepts `error?: boolean`. When `true`, sets `aria-invalid="true"` on the underlying checkbox input. Mirrors the pattern used by TextBox, TextArea, and ComboBox.

**Standalone (no FormField):** `useFormField()` returns an empty object; no change to existing standalone behaviour.

**Inside FormField:**
```tsx
<FormField name="terms" label="Agreements">
  <CheckBox label="I accept the terms" />
</FormField>
```
CheckBox automatically gains `aria-describedby` wired to the FormField hint/error elements.

**Prop-overrides-context rule:** `resolved = prop ?? contextValue ?? false`.

**Replaces:** CheckboxField (deprecated shim). See [CheckboxField.Semantic.md](./CheckboxField.Semantic.md) for migration guidance.
