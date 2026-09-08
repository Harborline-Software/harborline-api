# TextArea — Semantic Contract

- **Component:** TextArea
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./TextArea.Interaction.md) · [Accessibility](./TextArea.Accessibility.md) · [Styling](./TextArea.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TextArea.tsx`
- **Catalog row:** #133 TextArea (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<textarea>`

---

## 1. Component purpose

**TextArea** — a standalone multi-line text input with optional character counter, resize control, and auto-resize. Distinct from TextAreaField (which wraps TextArea inside FormField with label/hint/error). Built as a `forwardRef` component.

---

## 2. Props

```typescript
interface TextAreaProps extends Omit<React.TextareaHTMLAttributes<HTMLTextAreaElement>, 'onChange' | 'size'> {
  value?: string
  defaultValue?: string
  onChange?: (value: string) => void
  rows?: number               // default: 3
  resize?: 'none' | 'vertical' | 'horizontal' | 'both'  // default: 'vertical'
  autoResize?: boolean        // default: false — forces resize-none when true
  showCounter?: boolean       // default: false
  maxLength?: number
  size?: 'small' | 'medium' | 'large'             // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'         // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full' // default: 'medium'
  error?: boolean
}
```

All remaining `React.TextareaHTMLAttributes` (including `id`, `name`, `aria-*`, `disabled`, `readOnly`, `placeholder`) are passed through to the native `<textarea>` via `...props`.

---

## 3. onChange signature

`onChange` fires `string` (not `React.ChangeEvent`). The native `e.target.value` is extracted before calling the handler.

---

## 4. autoResize

When `autoResize=true`, the resize CSS class is forced to `resize-none`. Height auto-grow is NOT implemented in M1 — the prop is declared but has no JS resize logic.

---

## 5. showCounter

When `showCounter=true`, renders a character count overlay: `{len}/{maxLength}` when maxLength is set, or just `{len}` without it. `len` tracks the current value length via internal state.

## 6. Data model

TextArea supports both controlled (`value` + `onChange`) and uncontrolled
(`defaultValue`) modes. In controlled mode, `value` must be defined or React
will warn; initialise to `''` not `undefined` in controlled forms.

## 7. Form integration

The native `<textarea>` participates in HTML form submission normally. When
`name` is supplied (via passthrough), the textarea's current value is included
in the form data under that name. The submitted value is always a string.

Constraint validation (`required`, `minLength`, `maxLength`) works via native
HTML. TextArea does not suppress native validation bubbles.

## 8. autoResize — known gap

`autoResize=true` sets `resize-none` on the element but does NOT implement
height auto-grow in M1. The prop is reserved for future implementation.
Hosts that need grow-with-content today must implement their own JS resize
observer (`ref → textarea.scrollHeight` pattern).

## 9. resize vs autoResize precedence

When `autoResize=true`, the `resize` prop is ignored — CSS is forced to
`resize-none` regardless of the `resize` prop value.

## 10. Known gaps

| Gap ID | Severity | Description |
|---|---|---|
| G-TA1 | High | `autoResize` declared but unimplemented in M1 (§8) — hosts relying on auto-grow will get no resize |
| G-TA2 | Medium | Size prop accepts both `'small'/'medium'/'large'` and `'sm'/'md'/'lg'` aliases (Cohort-2 unification) |
| G-TA3 | Low | `showCounter` counter position not specified — implementations may render above/below/overlay differently |

---

## 11. FormField context integration (Cohort-2, 2026-06-12)

TextArea reads `FormFieldContext` via `useFormField()`. When wrapped in a `<FormField>`, the following props are automatically applied:

| Context value | Activates when | Prop precedence |
|---|---|---|
| `describedBy` | FormField has `hint` or `error` | No prop override — context-only |
| `required` | FormField has `required={true}` | Prop `required` wins if explicitly set |
| `disabled` | FormField has `disabled={true}` | Prop `disabled` wins if explicitly set |

**`invalid` prop deprecated (Cohort-2):** The `invalid` prop is retained as a one-release deprecated alias for `error`. When used, it emits `console.warn('TextArea: the \`invalid\` prop is deprecated. Use \`error\` instead.')` and maps to the error state. Use `error` for all new code.

**Standalone (no FormField):** `useFormField()` returns an empty object; no change to existing standalone behaviour.

**Inside FormField:**
```tsx
<FormField name="notes" label="Notes">
  <TextArea name="notes" />
</FormField>
```
TextArea automatically gains `aria-describedby` wired to the FormField hint/error elements.

**Prop-overrides-context rule:** `resolved = prop ?? contextValue ?? false`.

**Replaces:** TextAreaField (deprecated shim). See [TextAreaField.Semantic.md](./TextAreaField.Semantic.md) for migration guidance.
