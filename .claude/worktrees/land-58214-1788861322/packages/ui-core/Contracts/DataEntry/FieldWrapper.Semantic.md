# FieldWrapper — Semantic Contract

- **Component:** FieldWrapper
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FieldWrapper.Interaction.md) · [Accessibility](./FieldWrapper.Accessibility.md) · [Styling](./FieldWrapper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FieldWrapper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native HTML elements with CSS layout (no Radix primitive)

---

## 1. Purpose

FieldWrapper is a lightweight field layout shell that composes a label,
child input slot, optional hint text, and optional error message. It also
exports sub-components `Label`, `HintLabel`, and `ErrorLabel` for standalone
use. Unlike `FormField`, FieldWrapper does not use a `FormFieldContext`; it
passes layout and validity state to children via CSS descendant selectors
(`[&>input]:border-destructive`).

---

## 2. Data model

```typescript
interface FieldWrapperProps {
  label?: string
  hint?: string
  error?: string
  valid?: boolean
  optional?: boolean
  id?: string
  children?: React.ReactNode
  className?: string
}

interface LabelProps extends React.LabelHTMLAttributes<HTMLLabelElement> {
  optional?: boolean
  children?: React.ReactNode
  className?: string
}

interface HintLabelProps {
  children?: React.ReactNode
  className?: string
}

interface ErrorLabelProps {
  children?: React.ReactNode
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `label` | `string` | — | When provided, renders a `<Label>` with `htmlFor={id}` above the child slot. |
| `hint` | `string` | — | When provided (and `error` is absent), renders a `<HintLabel>` below the child slot. |
| `error` | `string` | — | When provided, renders an `<ErrorLabel>` with `role="alert"` below the child slot. Suppresses `hint` display when both are set. |
| `valid` | `boolean` | — | `true` applies success border treatment to child inputs/textareas/selects. `false` (or truthy `error`) applies error border treatment. `undefined` = no validity treatment. |
| `optional` | `boolean` | `false` | Passed to `<Label>`. When `true`, appends "(optional)" text after the label. |
| `id` | `string` | — | Passed to `<Label htmlFor>`. Should match the child input's `id`. |
| `children` | `ReactNode` | — | The input control(s) to render in the field slot. |
| `className` | `string` | — | Additional CSS classes on the root `<div>`. |

### 3.1 Validity state derivation

```typescript
const isInvalid = valid === false || Boolean(error)
```

Error border treatment activates when either `valid === false` OR `error` is a
non-empty string. Success border treatment (`[&>input]:border-success`) activates
only when `valid === true` (not just "not invalid").

### 3.2 Hint vs error priority

`hint` and `error` cannot both be visible at the same time:
- When `error` is provided: error message renders; hint is hidden.
- When only `hint` is provided: hint renders.

---

## 4. Sub-components

### Label

Renders a `<label>` with `text-sm font-medium leading-none`. When `optional` is
`true`, appends `<span className="ml-1 text-xs text-muted-foreground font-normal">(optional)</span>`.

### HintLabel

Renders a `<p>` with `text-xs text-muted-foreground`.

### ErrorLabel

Renders a `<p role="alert">` with `text-xs text-destructive`. The `role="alert"`
triggers an AT live announcement when the error appears.

---

## 5. Composition

FieldWrapper wraps its `children` in a `<div>` that applies descendant-based
CSS selectors. This means:

- Child `<input>`, `<textarea>`, and `<select>` elements receive the
  `border-destructive` class automatically when `isInvalid === true`.
- Child `<input>` and `<textarea>` receive `border-success` when `valid === true`.
- No `FormFieldContext` is provided — child components that read from
  `useFormField()` will not receive hints from FieldWrapper.

---

## 6. Deferred features

- **`FormFieldContext` integration.** FieldWrapper does not provide context.
  Child components that use `useFormField()` (e.g. SearchField) need to be
  wrapped in `FormField`, not FieldWrapper, to receive `describedBy`.
- **Per-field `aria-describedby` wiring.** No `id`-based hint/error description
  is threaded to children; hint/error are visually below the child only.
