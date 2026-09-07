# FormField — Semantic Contract

- **Component:** FormField
- **ADR 0017 family:** DataEntry _(maps to the ADR 0017 "Forms" family — DataEntry is the per-input subdirectory under the same family)_
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FormField.Interaction.md) · [Styling](./FormField.Styling.md) · [Accessibility](./FormField.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormField.tsx` + `FormFieldContext.tsx`
- **Catalog rows:** #55 FieldWrapper / #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Radix UI `@radix-ui/react-label`

---

## 1. Purpose

FormField is the canonical **field-wrapper** of `@harborline-software/ui-react`. It pairs a
labelled label-row (label text + required-marker) with a child input (or any
focusable form control), threads a `FormFieldContext` to the child so the child
can wire `aria-describedby` to the hint and error nodes, and renders the hint
text and/or the error message below the input.

FormField is the **WCAG 2.2 AA compliance mechanism** for `@harborline-software/ui-react`
inputs: every form control wrapped in a FormField gets a programmatic label
linkage (via `<label htmlFor>`), an `aria-describedby` linkage to its hint /
error, and the visual error treatment when `error` is supplied.

The contract uses Radix UI's `@radix-ui/react-label` `Label.Root` primitive
under the hood; the contract is framework-neutral in spirit but the
implementation note is documented here for reverse-spec accuracy.

---

## 2. Data model

FormField is a presentational composition. It owns no input state itself; the
host owns the child input's value. FormField's only runtime concern is wiring
ids:

```typescript
// Internal id-construction rules (informative; not exposed as props):
//   hintId  = hint  ? `${name}-hint`  : undefined
//   errorId = error ? `${name}-error` : undefined
//   describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined
//
// The describedBy string is threaded to the child input via FormFieldContext;
// the child reads it via the useFormField() hook (see §6 Context API).
```

```typescript
interface FormFieldProps {
  label: string
  name: string
  required?: boolean
  hint?: string
  error?: string
  children: React.ReactNode
}
```

```typescript
// FormFieldContext (consumed by the child input via useFormField())
interface FormFieldContextValue {
  id?: string       // input element id — set by FormField, read by child to wire htmlFor
  describedBy?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `label` | `string` | _required_ | The label text rendered above the input. Used as the `<label>` element's text content. |
| `name` | `string` | _required_ | The field's name. Drives the `htmlFor` attribute on the label, and is the **prefix** for the hint/error ids (`{name}-hint`, `{name}-error`). Child inputs must set `id={name}` on their root focusable element for label linkage to work — see §4. |
| `required` | `boolean` | `false` | When `true`, renders a red asterisk (`*`) after the label. The asterisk has `aria-hidden="true"` because requiredness is a property of the input itself (the child input should also set `required` so screen readers announce it). |
| `hint` | `string` | — | Optional helper text rendered below the input. When `error` is supplied, hint remains programmatically associated (`describedBy` keeps the hint id) and may remain visibly rendered as secondary supporting text when it materially helps the user fix the error (e.g. a format example). |
| `error` | `string` | — | Optional error message rendered below the input with `role="alert"`. Presence puts FormField into "error mode" — error text renders as the primary supporting line; hint text is secondary. Both `{name}-hint` and `{name}-error` ids are present in `describedBy`. |
| `children` | `ReactNode` | _required_ | The input (or any focusable form control). The child reads `describedBy` from `FormFieldContext` via `useFormField()`. |

### 3.1 Label-input linkage (`htmlFor` ↔ `id` contract)

The label renders with `htmlFor={name}`. Child inputs MUST render their root
focusable element with `id={name}` for the programmatic linkage to land. Every
`@harborline-software/ui-react` field component (TextField, SelectField, DateField,
NumberField, CheckboxField) honours this contract — see the per-component
Semantic contracts.

### 3.2 `describedBy` and hint-error coexistence (RA-12)

When both `hint` and `error` are supplied, the `describedBy` string contains
**both** ids (`"{name}-hint {name}-error"`). Both nodes are rendered in the DOM
so neither id is dangling — the prior M1 wart (suppressing hint in error mode,
leaving a dangling hint id) is resolved.

**Rendering rule:** error text renders as the primary supporting line (below
the input). Hint text may also render as a secondary supporting line when it
provides corrective guidance the user needs to fix the error (e.g., a format
pattern like "YYYY-MM-DD"). The implementation decides visibility; the
contract guarantees both are programmatically associated.

Council ruling RA-12 (2026-06-06): "FormField surfaces error text in place of
standard supporting text by default. Hint text remains programmatically
associated, and may remain visibly rendered as secondary supporting text when
needed to help the user fix the error."

### 3.3 HTML attribute passthrough

The current implementation does **not** spread arbitrary HTML attributes on the
root `<div>` wrapper. Known gap; future amendment.

---

## 4. Events — semantics

FormField has **no event surface**. It is a structural / presentational
component. Event surfaces (`onChange`, `onBlur`, etc.) belong to the child
input components — see TextField / SelectField / DateField / NumberField /
CheckboxField Semantic contracts.

---

## 5. Slots

| Slot | Purpose | Relationship to props |
| --- | --- | --- |
| `children` | The wrapped input. | Required. The child reads `describedBy` via `useFormField()`. |

Custom slots for the label text (e.g. a tooltip-bearing label), the
required-marker, or the hint/error nodes are **not exposed** in M1. Hosts that
need custom label content compose the FormField wholesale; see Open Questions
(§8 #1).

---

## 6. Context API — `FormFieldContext` and `useFormField()`

FormField is the **provider** for `FormFieldContext`; child inputs are the
**consumers** via the `useFormField()` hook. The contract surface is:

```typescript
// Provider — internal to FormField; not part of the consumer API.
<FormFieldProvider value={{ id, describedBy }}>
  {children}
</FormFieldProvider>

// Consumer — used by every field child component.
import { useFormField } from './FormFieldContext'

function MyCustomField({ ... }) {
  const { id, describedBy } = useFormField()
  return <input id={id} aria-describedby={describedBy} ... />
}
```

The `useFormField()` hook returns `{ id?: string; describedBy?: string }`. It
is **safe to call outside a FormField** — the default context value is `{}`, so
both fields are `undefined` and the child input falls back to its own id/prop.

This loose-coupling is intentional: hosts can use `@harborline-software/ui-react` field
components standalone (without a FormField wrapper) for cases that don't need
the label-row treatment, and the field components still render correctly.

---

## 7. Component composition

- **Field child components.** TextField, SelectField, DateField, NumberField,
  and CheckboxField all consume `useFormField()` to read `describedBy`. Each
  also accepts a `name` prop that the host must align with the FormField's
  `name` for `htmlFor` ↔ `id` linkage.
- **Form layouts.** A typical form is a vertical stack of FormField instances,
  each wrapping one input. FormField does not own layout above itself (no
  margins between sibling FormFields) — the host's form layout owns that.
- **Standalone field usage.** Field components can render without a FormField
  wrapper for unlabelled or inline use (e.g. a filter input in a toolbar).
  In that mode `describedBy` is `undefined` and no `aria-describedby` is set.

---

## 8. Deferred features

Out of scope for the M1 baseline:

- **Custom label slot** — `label` is currently `string`; a `ReactNode` slot
  would let hosts add tooltips, info icons, or links to documentation in the
  label row.
- **Per-field layout direction** — current layout is fixed (label above
  input, hint/error below). Inline labels (label-left, input-right) would
  need a `layout?: 'stacked' | 'inline'` prop.
- **HTML attribute passthrough on root.**
- **Multi-error / list-of-errors** — current `error` is a single `string`;
  a `string[]` would let validators surface multiple errors per field.

---

## 9. Open questions (for council)

1. **Custom label slot.** Should `label` accept `string | ReactNode` to allow
   tooltips, info icons, or links? Today it's `string`-only. (Leaning: keep
   `string` for M1 — opt for a separate `labelHint` or `labelAffix` prop in
   a later wave if real demand emerges.)
2. **`describedBy` dangling id in error mode.** RESOLVED (RA-12, 2026-06-06) — both ids are always kept in `describedBy`; both nodes are rendered in the DOM; hint is secondary supporting text in error mode, not hidden.
3. **Required-marker accessibility.** The asterisk has `aria-hidden="true"`
   and the contract relies on the child input's own `required` attribute to
   surface required state to AT. Confirm with PAO Accessibility that this
   division of labour is correct, or pull the required-state announcement
   into the FormField's `<label>` (e.g. via a visually-hidden "required"
   text).

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |

---

## Wave FR-1.4 — FormFieldContext required + disabled expansion

_Ruling: FR-1.4 (family-rulings-2026-06-11.md). Pattern: DataGrid #1022._
_Status: Draft._

### FR-1.4.1 FormFieldContextValue shape (expanded)

FR-1.4 adds `required` and `disabled` to the context value alongside the
existing `id`, `inputId`, and `describedBy` fields:

```typescript
interface FormFieldContextValue {
  id?: string
  inputId?: string
  describedBy?: string
  required?: boolean   // NEW — FR-1.4
  disabled?: boolean   // NEW — FR-1.4
}
```

**Default values.** The context default object remains `{}`. Both new fields
are absent (i.e. `undefined`) in the default, which is identical to `false`
for consumers that use `contextValue.required ?? false`.

**Back-compat.** Both fields are optional. Any child input that was
produced before this wave and does not read these fields continues to work
unchanged: the runtime context object grows two new keys that the consumer
silently ignores.

### FR-1.4.2 FormFieldProps (expanded)

FormField gains two new optional props that thread into the context:

```typescript
interface FormFieldProps {
  label: string
  name: string
  required?: boolean    // existing — drives asterisk + context.required (FR-1.4)
  disabled?: boolean    // NEW — FR-1.4 — context.disabled only; no FormField-level visual dimming
  hint?: string
  error?: string
  children: React.ReactNode
}
```

**`required` threading.** The existing `required` prop already drives the
required-asterisk visual. FR-1.4 additionally threads it into
`FormFieldContextValue.required` so that child inputs can read it from
context without requiring the host to pass `required` twice.

**`disabled` threading.** The new `disabled` prop does NOT apply any visual
dimming to FormField's own chrome (label, hint, error nodes). Visual dimming
for the `disabled` state is per-control responsibility — each child input
owns its own dimmed styling when disabled. FormField's only responsibility is
to thread `disabled: true` into the context so child inputs can pick it up.

The internal provider call becomes:

```typescript
<FormFieldProvider
  value={{
    id: name,
    describedBy: describedBy || undefined,
    required: required ?? false,   // FR-1.4
    disabled: disabled ?? false,   // FR-1.4
  }}
>
  {children}
</FormFieldProvider>
```

### FR-1.4.3 Consumption rule for child inputs

All `@harborline-software/ui-react` field inputs that consume `useFormField()` MUST apply
the following combination rule when deriving their effective `required` and
`disabled` values:

```typescript
const { required: ctxRequired, disabled: ctxDisabled } = useFormField()

const effectiveRequired = props.required || ctxRequired   // logical OR
const effectiveDisabled = props.disabled || ctxDisabled   // logical OR
```

**Rationale for logical OR (not override).** A child input may be disabled
individually (e.g., a date field locked pending another input) even when the
parent FormField is not. Conversely, a FormField marked `disabled` must
disable all its children regardless of per-child props. Logical OR satisfies
both cases without requiring child inputs to know which source fired.

**Back-compat.** When neither the FormField nor the child declares
`disabled`/`required`, both context fields are `false`/`undefined` and the
logical OR resolves to the child's local prop — identical to current
behaviour.

**`aria-required` emission.** When `effectiveRequired` is `true`, the child
input MUST emit `aria-required="true"` on its root focusable element (or use
the native `required` attribute on a native `<input>`). FormField's asterisk
is `aria-hidden="true"` and does NOT satisfy this ARIA requirement; the child
input is the sole ARIA source.

### FR-1.4.4 ESignatureField migration note

ESignatureField.Semantic §7 documents that it consumes `required`, `disabled`,
and `error` from context. Prior to this wave those fields did not exist on
`FormFieldContextValue`. FR-1.4 is the structural fix that closes that gap.

When FR-1.4 ships:

- The `describedBy`-substring heuristic that ESignatureField used to infer
  `required` state from the `aria-describedby` value (if any existed in the
  implementation) is retired. `FormFieldContext.required` is the canonical
  channel.
- ESignatureField's implementation may be updated in the same PR or in a
  follow-on; the context shape is now guaranteed, so the update is
  mechanical.

_Cross-reference: ESignatureField.Semantic §7 (supersession note added
2026-06-11 per FR-1.4)._
