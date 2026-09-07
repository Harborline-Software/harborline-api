# FormController — Semantic Contract

- **Component:** FormController
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Draft
- **Companion contracts:** [Interaction](./FormController.Interaction.md) · [Styling](./FormController.Styling.md) · [Accessibility](./FormController.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormController.tsx`

---

## 1. Purpose

`FormController` is the **headless** controlled-form manager (UC-1). It owns
field values, validation state, and submission eligibility, and renders **no
DOM of its own** — it exposes a render-props object via a `render` prop or
function-as-`children`. It is the behavioural counterpart to the M1 `Form`
layout container: hosts compose the two together (layout from `Form`, state from
`FormController`).

The controller derives the canonical form flags (`valid`, `modified`,
`touched`, `visited`, `submitted`, `canSubmit`), runs field-level and form-level
validators, and provides programmatic controls (`setValue`, `getValue`,
`submit`, `reset`, `markVisited`, `markTouched`). UC-2/UC-3 are deferred: a
`Field` sub-component, `FormRef`, `validateOn`, and externally-injected errors
are out of scope for this wave.

## 2. Data model

```typescript
export type FormValidatorType<T extends Record<string, unknown>> = (
  values: T,
  getValue: (name: keyof T) => unknown,
) => Partial<Record<keyof T, string>>

export type FieldValidatorType<V = unknown> = (
  value: V,
  values: Record<string, unknown>,
) => string | null

export interface FormControllerProps<T extends Record<string, unknown>> {
  initialValues: T
  onSubmit: (values: T, event?: React.SyntheticEvent) => void
  validator?: FormValidatorType<T>
  fieldValidators?: Partial<Record<keyof T, FieldValidatorType>>
  ignoreModified?: boolean
  onChange?: (name: keyof T, value: unknown, getValue: (name: keyof T) => unknown) => void
  render?: (props: FormControllerRenderProps<T>) => React.ReactNode
  children?: (props: FormControllerRenderProps<T>) => React.ReactNode
}

export interface FormControllerRenderProps<T extends Record<string, unknown>> {
  canSubmit: boolean
  valid: boolean
  modified: boolean
  touched: boolean
  visited: boolean
  submitted: boolean
  errors: Partial<Record<keyof T, string>>
  setValue: (name: keyof T, value: unknown) => void
  getValue: (name: keyof T) => unknown
  submit: (event?: React.SyntheticEvent) => void
  reset: () => void
  markVisited: (name: keyof T) => void
  markTouched: (name: keyof T) => void
}
```

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `initialValues` | `T` (required) | — | Baseline snapshot. `reset()` returns to it; changing its identity recomputes `modified` and re-runs validators against the new baseline. |
| `onSubmit` | `(values, event?) => void` (required) | — | Called only when `canSubmit` is true. |
| `validator` | `FormValidatorType<T>` | — | Form-level validator; runs on submit after field-level validators. Form-level errors override field-level. |
| `fieldValidators` | `Partial<Record<keyof T, FieldValidatorType>>` | — | Per-field validators; each runs on every change to its field (full `values` passed for cross-field rules). |
| `ignoreModified` | `boolean` | `false` | When true, `canSubmit` no longer requires `modified` (re-confirm dialogs, filter forms, pre-populated edit forms). |
| `onChange` | `(name, value, getValue) => void` | — | Observer fired **after** internal state updates. Not a controller — use `fieldValidators` to reject values. |
| `render` | `(props) => ReactNode` | — | Render-prop form. Takes precedence over `children`. |
| `children` | `(props) => ReactNode` | — | Function-as-children form (same signature as `render`). |

## 4. Events

| Event | Payload | When |
| --- | --- | --- |
| `onSubmit` | `(values: T, event?)` | On a `submit()` call (or wired form submit) **only when `canSubmit`**. |
| `onChange` | `(name, value, getValue)` | After every `setValue`, post state-update — an observer, never a gate. |

## 5. Slots

`render` / `children` — a single function-as-children slot receiving
`FormControllerRenderProps<T>`. `render` wins when both are supplied. There are
no visual slots — the controller is headless and emits a fragment around the
function's return.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
