# Form — Semantic Contract

- **Component:** Form
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Form.Interaction.md) · [Styling](./Form.Styling.md) · [Accessibility](./Form.Accessibility.md)
- **Related contracts:** [FormField.Semantic.md](./FormField.Semantic.md) — per-field label/hint/error wrapper composing inside Form.
- **Reference implementation:** `packages/ui-react/src/components/forms/Form.tsx`
- **Catalog row:** #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<form>` element wrapper

---

## 1. Purpose

Form is the canonical **form layout container** of `@harborline-software/ui-react`. It
renders a native HTML `<form>` element, provides a vertically-stacked layout
for FormField / input rows, and exposes the form's `onSubmit` lifecycle. It
is intentionally **validation-library-agnostic** — it does not import or
require react-hook-form, Zod, or any other validation library. The host
owns validation state and threads it into individual FormField + input props.

Form's role is structural:

1. **DOM boundary** — wraps fields in a `<form>` so native browser semantics
   work (Enter-to-submit, button[type=submit], `:invalid` pseudo-class, etc.).
2. **Layout** — applies the inter-field spacing and (optionally) side-by-side
   horizontal layout for label + input columns.
3. **Submit lifecycle** — exposes `onSubmit` (with `preventDefault` applied
   before the callback fires) so the host does not have to intercept the
   native event manually.

Form is typically composed with a footer slot holding Cancel + Submit Button
instances.

---

## 2. Data model

Form has no validation state. It is a presentational container.

```typescript
type FormLayout = 'stacked' | 'horizontal'

interface FormProps extends Omit<React.FormHTMLAttributes<HTMLFormElement>, 'onSubmit'> {
  layout?: FormLayout
  labelWidth?: string | number
  gap?: 'sm' | 'md' | 'lg'
  onSubmit?: (e: React.FormEvent<HTMLFormElement>) => void
  children: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `layout` | `'stacked' \| 'horizontal'` | `'stacked'` | Form layout mode (see §3.1). `'stacked'` stacks label above input (default); `'horizontal'` places label to the left of the input. |
| `labelWidth` | `string \| number` | `'180px'` | Horizontal-layout only. Controls the fixed width of the label column so inputs align. Accepts any CSS width value (`'120px'`, `'30%'`). Ignored in `'stacked'` mode. |
| `gap` | `'sm' \| 'md' \| 'lg'` | `'md'` | Vertical gap between FormField rows. Tokens owned by PAO Styling. |
| `onSubmit` | `(e: FormEvent) => void` | — | Fires when the form submits. **`event.preventDefault()` is called automatically before the callback** — the host does not need to call it. When omitted, native form submission proceeds. |
| `children` | `ReactNode` | _required_ | Form content. Typically a composition of FormField rows + a footer with action buttons. |
| HTML attributes | — | — | Spread onto the root `<form>`. Includes `action`, `method`, `encType`, `autoComplete`, `noValidate`, `id`, `aria-*`, `data-*`, etc. |

### 3.1 Layout modes

**`stacked` (default):**
```
Label
[Input field]
   hint text / error message

Label
[Input field]
```

**`horizontal`:**
```
Label   [Input field]
           hint text / error message

Label   [Input field]
```

In horizontal mode the `labelWidth` value is set as a CSS custom property on
the root element so the FormField child can consume it for alignment. PAO
Styling owns the CSS implementation.

### 3.2 `onSubmit` and `preventDefault`

Form calls `event.preventDefault()` before firing `onSubmit`. This prevents
the browser from navigating the page — SPA forms never want native page reload.

```tsx
// Host code — no e.preventDefault() needed:
<Form onSubmit={(e) => { void handleSave(formData) }}>
  …
</Form>
```

When `onSubmit` is omitted, `preventDefault` is **not** called — native form
submission (to `action` target) proceeds as expected. This makes Form
usable in progressive-enhancement or server-side-rendered scenarios.

### 3.3 Validation-library integration

Form is validation-library-agnostic. Typical host patterns:

- **react-hook-form:** wrap the entire Form in `<FormProvider>` from RHF,
  then access `handleSubmit` via the form context. Pass `handleSubmit(onValid)`
  to Form's `onSubmit`.
- **Zod:** host drives a `useForm` from RHF+Zod resolver; the Form component
  is unaware.
- **Manual validation:** host maintains `errors` state, threads individual
  error strings into FormField `error` props, and validates on `onSubmit`.

No adapter or context is provided by Form itself for validation state.

### 3.4 HTML attribute passthrough

Form spreads all valid HTML form attributes onto the `<form>` element (see
`FormProps` extends `React.FormHTMLAttributes<HTMLFormElement>`). The `onSubmit`
prop is remapped (§3.2); all other form attributes are forwarded verbatim.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onSubmit` | `React.FormEvent<HTMLFormElement>` | The form submits — Enter in a text input, button[type="submit"] click, or programmatic `.requestSubmit()`. `event.preventDefault()` has already been called before the callback receives the event. |

No `onChange`, `onReset`, or `onInvalid` props in M1. Hosts route those via
HTML attribute passthrough.

---

## 5. Slots

Form uses `children` as its single slot. The host composes:

- **FormField + input rows** as the body.
- **An action footer** (typically a `<div>` with `flex justify-end gap-2`) as
  the last child, holding Cancel and Submit Button instances.

No named sub-components — Form is a single-region container. The action footer
pattern is by convention, not enforced by the component.

---

## 6. Component composition

### Standard CRUD form

```tsx
<Form onSubmit={handleSubmit(onValid)}>
  <FormField label="Property name" name="name" required error={errors.name?.message}>
    <TextField name="name" value={watch('name')} onChange={v => setValue('name', v)} error={!!errors.name} />
  </FormField>
  <FormField label="City" name="city">
    <TextField name="city" value={watch('city')} onChange={v => setValue('city', v)} />
  </FormField>
  <div className="flex justify-end gap-2 pt-4">
    <Button variant="ghost" onClick={() => router.back()}>Cancel</Button>
    <Button type="submit" variant="primary" loading={isSubmitting}>Save</Button>
  </div>
</Form>
```

### Dialog-embedded form

A form inside a Dialog footer is a common pattern. The Dialog's `footer` slot
holds the action buttons; the Dialog body holds the Form. The Form's `onSubmit`
closes the dialog on success.

```tsx
<Dialog
  open={open}
  onOpenChange={setOpen}
  title="Add vendor"
  footer={<>
    <Button variant="ghost" onClick={() => setOpen(false)}>Cancel</Button>
    <Button type="submit" form="vendor-form" variant="primary" loading={isSaving}>Save</Button>
  </>}
>
  <Form id="vendor-form" onSubmit={handleSubmit(onValid)}>
    <FormField label="Name" name="vendorName" required error={errors.vendorName?.message}>
      <TextField name="vendorName" value={watch('vendorName')} onChange={v => setValue('vendorName', v)} error={!!errors.vendorName} />
    </FormField>
  </Form>
</Dialog>
```

Note: the `form="vendor-form"` attribute on the external Submit Button links
it to the Form via the native HTML `form` attribute — standard HTML, no React
magic required.

### Horizontal label-aligned form

```tsx
<Form layout="horizontal" labelWidth="150px">
  <FormField label="First name" name="firstName">
    <TextField name="firstName" value={firstName} onChange={setFirstName} />
  </FormField>
  <FormField label="Last name" name="lastName">
    <TextField name="lastName" value={lastName} onChange={setLastName} />
  </FormField>
</Form>
```

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Column layout** — multi-column form grids (e.g. a 2-column layout where
  two fields sit side by side). Hosts compose via grid children today.
- **`disabled` propagation** — a Form-level `disabled` prop that visually
  disables all child inputs. Currently there is no context mechanism; hosts
  must pass `disabled` to each field individually.
- **`loading` state** — Form-level loading that prevents submit and shows
  a loading indicator on the submit button. Hosts drive Button's `loading`
  prop directly.
- **Inline validation mode** — validation triggered on field blur rather
  than form submit. This is a host concern (react-hook-form `mode: 'onBlur'`
  or manual `onBlur` handlers).
- **`onReset` callback** — fires when the form resets. Hosts route via HTML
  attribute passthrough today.
- **Section grouping** — a `<FormSection>` sub-component for grouping
  related fields under a heading. Hosts compose with headings manually.

---

## 8. Open questions (for council)

1. **`disabled` propagation via context.** Should Form expose a context
   that disables all child FormField + inputs when a `disabled` prop is set?
   This is how Telerik's Form works — a single `Enabled={false}` disables all
   fields. Leaning yes for M1.1 — individual prop threading is verbose.
2. **`gap` vs `className`.** Three-value `gap` prop vs Tailwind `className`
   passthrough for spacing. Leaning keep `gap` — it's the most common
   customization and naming it explicitly signals design intent.
3. **Horizontal-layout `labelWidth`.** Should this be a prop or a CSS
   variable the host sets? Leaning prop — keeps it co-located with the
   layout declaration.
4. **Footer slot.** Should Form have an explicit `footer` prop (like Dialog)
   to render actions with consistent spacing? Or keep it `children`-only?
   Leaning children-only for M1 — the host controls the action row's content
   and alignment.
5. **`noValidate` default.** When hosts use react-hook-form/Zod, the browser's
   native `required`/`pattern` validation bubbles interfere. Should Form
   default to `noValidate={true}`? Leaning no — changing the browser default
   is a footgun for server-rendered or progressive-enhancement use cases.
   Hosts add `noValidate` via HTML attribute passthrough when needed.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |

---

## Wave UC-1 — Form controller model (structural expansion)

_Ruling: FR-1 (family-rulings-2026-06-11.md). Kendo minimum surface per 2026-06-12 re-audit._
_Status: Draft._

### UC-1.1 Motivation

The M1 baseline (§2–§6 above) specifies Form as a **pure layout container** —
a `<form>` wrapper with `onSubmit` passthrough. This is correct and shipped.
However the 2026-06-12 re-audit found no contract covering the
**form-as-controller** pattern: the variant where the Form component also
manages field values, validation state, and submission eligibility (instead of
delegating entirely to react-hook-form / Zod / manual state).

Kendo's Form component embodies this pattern. The UC-1 expansion specifies the
minimum controller surface needed for Kendo parity so the polish gate for the
Form family can be satisfied without per-session re-audit churn.

**What this expansion is NOT:** UC-1 does NOT mandate that the existing M1
`Form` component be rewritten as a controlled form manager. The M1 Form stays
as-is. UC-1 specifies an ADDITIONAL export — `FormController` — that hosts
can use when they want managed field state without an external library. The
M1 `Form` is still the right choice for hosts that bring their own state
(react-hook-form, Zod resolver, manual `useState`).

### UC-1.2 Vocabulary mapping — Kendo vs our FR-1 rulings

The following table is the canonical reference for implementers and auditors.
Re-audits MUST consult this table before flagging a discrepancy as a missing
feature — Kendo naming differences from our vocabulary are resolved here, not
in implementation code.

| Kendo term | Our vocabulary | Notes |
|---|---|---|
| `FormProps.initialValues` | `FormController` `initialValues` prop | Direct carry-over; same shape. |
| `FormProps.onSubmit` (receives typed `values`) | `FormController` `onSubmit` | `values` is typed as `T` (generic parameter). |
| `FormProps.validator` | `FormController` `validator` (form-level) | `FormValidatorType<T>`: `(values: T, valueGetter: (name: keyof T) => unknown) => Partial<Record<keyof T, string>>` |
| `FormProps.onChange` (form-level field-change observer) | `FormController` `onChange` | Observes field changes; does not replace the field's own `onChange`. |
| `FormRenderProps.allowSubmit` | `renderProps.canSubmit` | Renamed to avoid confusion with HTML button semantics; same meaning: `valid && (modified \|\| ignoreModified)`. |
| `FormRenderProps.valid` | `renderProps.valid` | `true` when all validators pass. Maps to `!error` on individual fields per FR-1.2. |
| `FormRenderProps.modified` | `renderProps.modified` | Any field value diverged from `initialValues`. |
| `FormRenderProps.touched` | `renderProps.touched` | Any field has been blurred at least once. |
| `FormRenderProps.visited` | `renderProps.visited` | Any field has been focused at least once. |
| `FormRenderProps.errors` | `renderProps.errors` | `Partial<Record<keyof T, string>>` — field path → error message string. |
| `FormRenderProps.onChange` (programmatic setter) | `renderProps.setValue` | Renamed to distinguish from the field-level `onChange` event; same purpose. |
| `FormRenderProps.onSubmit` (programmatic trigger) | `renderProps.submit` | Renamed for imperative clarity. |
| `FormRenderProps.onFormReset` | `renderProps.reset` | Reverts all field values to `initialValues`. |
| `FormRenderProps.valueGetter` | `renderProps.getValue` | Retrieves current field value by path. |
| `FormRenderProps.submitted` | `renderProps.submitted` | `true` after a successful `onSubmit` call. |
| `FieldRenderProps.validationMessage` | field `error: string \| null` prop (composed via FormField) | FR-1.3: validation messages stay in the composition layer (FormField / ValidationSummary), NOT as a per-input `validationMessage` prop. The controller supplies `errors[name]` which the host threads into FormField `error`. |
| `FieldRenderProps.valid` | `!error` at the input level (FR-1.2) | `valid` is `true` when the controller's `errors[name]` is absent or null. |
| `FormProps.ignoreModified` | `FormController` `ignoreModified` | When `true`, `canSubmit` does not require `modified === true`. |

**Key principle (FR-1.3):** Kendo achieves validation-message display by
threading `FieldRenderProps.validationMessage` directly into the field
component. Our vocabulary achieves the same end result via **composition**:
the controller exposes `errors` in the render props; the host threads
`errors[name]` into the `FormField` `error` prop; `FormField` renders the
message and wires `aria-describedby` per its existing contract. Auditors
reviewing a Harborline form that does not have a `validationMessage` prop on
inputs should not flag this as a gap — it is the FR-1.3 composition approach.

### UC-1.3 FormController props (first wave)

```typescript
// Wave UC-1 export — a controlled form manager.
// Counterpart to the existing M1 Form (layout-only container).

type FormValidatorType<T extends Record<string, unknown>> = (
  values: T,
  getValue: (name: keyof T) => unknown,
) => Partial<Record<keyof T, string>>

type FieldValidatorType<V = unknown> = (
  value: V,
  values: Record<string, unknown>,
) => string | null

interface FormControllerRenderProps<T extends Record<string, unknown>> {
  // State flags
  canSubmit: boolean         // valid && (modified || ignoreModified)
  valid: boolean             // all validators pass
  modified: boolean          // any field diverged from initialValues
  touched: boolean           // any field blurred at least once
  visited: boolean           // any field focused at least once
  submitted: boolean         // onSubmit completed at least once this session

  // Error map — host threads into FormField error props
  errors: Partial<Record<keyof T, string>>

  // Programmatic controls
  setValue: (name: keyof T, value: unknown) => void
  getValue: (name: keyof T) => unknown
  submit: (event?: React.SyntheticEvent) => void
  reset: () => void

  // Field interaction signals — host-wired focus/blur hooks
  // (UC-2 Field sub-component will call these automatically;
  //  in UC-1 Option A the host wires them from input event handlers)
  markVisited: (name: keyof T) => void  // call from input onFocus
  markTouched: (name: keyof T) => void  // call from input onBlur
}

interface FormControllerProps<T extends Record<string, unknown>> {
  // Initial field values. Resetting returns to this snapshot.
  initialValues: T

  // Typed submission handler. Only called when valid (or ignoreModified=true).
  onSubmit: (values: T, event?: React.SyntheticEvent) => void

  // Form-level validator. Runs after all field-level validators.
  // Returns a partial errors map; fields absent from the return are considered valid.
  validator?: FormValidatorType<T>

  // When true, canSubmit does not require modified===true.
  // Use for forms that may be submitted unchanged (e.g. re-confirm dialogs).
  ignoreModified?: boolean

  // Observe field value changes without replacing the field's own onChange.
  onChange?: (
    name: keyof T,
    value: unknown,
    getValue: (name: keyof T) => unknown,
  ) => void

  // Render-prop form. Receives FormControllerRenderProps.
  // Use render OR children — not both.
  render?: (props: FormControllerRenderProps<T>) => React.ReactNode

  // children-as-function form. Same signature as render.
  children?: (props: FormControllerRenderProps<T>) => React.ReactNode
}
```

**render vs children:** Both forms are equivalent. `render` matches Kendo's
convention and is preferred for new code when the children slot needs to remain
for `FormField` composition. `children`-as-function is the React community
convention; both are supported.

**`markVisited` and `markTouched` — host-wiring hooks:**

These two render-prop functions let host-wired inputs signal focus and blur
events up to the controller so the aggregate `visited` / `touched` flags work
correctly without a UC-2 `Field` sub-component.

| Function | Signature | When the host should call it |
|---|---|---|
| `markVisited` | `(name: keyof T) => void` | From the input's `onFocus` handler, with the field's name. Contributes to the aggregate `visited` flag. |
| `markTouched` | `(name: keyof T) => void` | From the input's `onBlur` handler, with the field's name. Contributes to the aggregate `touched` flag. |

Both functions are **idempotent**: calling `markVisited('email')` twice has the
same effect as calling it once — the per-field set is additive only (no
per-field unfocus tracking in UC-1). Repeated calls for the same field name
cause no re-render.

Relation to the aggregate flags:
- `visited` (form-level) is `true` when at least one field has been passed to
  `markVisited`. It is cleared by `reset()`.
- `touched` (form-level) is `true` when at least one field has been passed to
  `markTouched`. It is cleared by `reset()`.

Option A host wiring example:

```tsx
<FormController initialValues={{ email: '' }} onSubmit={handleSave}>
  {({ getValue, setValue, markVisited, markTouched, errors }) => (
    <FormField label="Email" name="email" error={errors.email}>
      <input
        value={getValue('email') as string}
        onChange={e => setValue('email', e.target.value)}
        onFocus={() => markVisited('email')}
        onBlur={() => markTouched('email')}
      />
    </FormField>
  )}
</FormController>
```

When UC-2 `Field` ships, it will call `markVisited` and `markTouched`
automatically from its own `onFocus`/`onBlur` wiring. Hosts using Option A
in UC-1 must wire them manually as shown above.

**onSubmit guard:** `FormController` calls `onSubmit` only when `canSubmit` is
`true`. Hosts that need to observe every submit-button click regardless of
validity (e.g. to trigger visual error feedback before valid state is reached)
use `renderProps.submit` wrapped in their own guard, or add a Button
`onClick` handler.

### UC-1.4 State semantics — dirty, modified, touched, visited, reset

These four flags originate in Kendo and are the source of the most
recurring audit confusion. Canonical definitions:

| Flag | Set when | Cleared when |
|---|---|---|
| `visited` | The host calls `markVisited(name)` (typically from the input's `onFocus`). | `reset()` is called. |
| `touched` | The host calls `markTouched(name)` (typically from the input's `onBlur`). | `reset()` is called. |
| `modified` | A field's value differs from its `initialValues` entry. | `reset()` is called OR `initialValues` prop changes (see below). |
| `valid` | Every active validator returns no error. | A validator returns an error string. |
| `canSubmit` | `valid && (modified \|\| ignoreModified)`. | Either condition becomes false. |
| `submitted` | `onSubmit` callback returns without throwing. | `reset()` is called. |

**Aggregate semantics:** the form-level `touched` / `visited` / `modified`
flags are the logical OR across all registered fields. A single modified field
makes `modified` true; a single unblurred field does NOT make `touched` false
(the aggregate is still true as long as one field has been blurred).

**`modified` and `initialValues` changes:** When the `initialValues` prop
changes (e.g. the host fetches updated data and re-supplies it), `modified`
is recomputed from the new baseline. Fields whose current value equals the new
initial value are no longer considered modified. This matches Kendo behavior.

**`reset()` semantics:** `reset()` reverts ALL field values to the current
`initialValues` snapshot and clears `visited`, `touched`, `submitted`, and
derived flags. It does NOT trigger `onSubmit`. Form-level and field-level
validators re-run against the reset values to update `valid` and `errors`.

**`ignoreModified`:** When `true`, `canSubmit` becomes `valid` (without the
`modified` gate). Use for forms where the user may need to re-submit unchanged
values — e.g. a re-confirm dialog, a filter form that should apply on first
open, or an edit form pre-populated from server data where the user might want
to save without changing anything.

### UC-1.5 Layering with FormField / FormFieldContext (FR-1.4)

`FormController` is the **value + validation orchestration layer**. It sits
ABOVE `FormField`. The relationship:

```
FormController (value state, validation, canSubmit, errors)
  └── <Form layout="stacked"> (M1 layout container)
        └── FormField (label, hint, error, FR-1.4 required/disabled context)
              └── TextField / SelectField / DateField / … (FR-1 inputs)
```

`FormController` does NOT replace or extend `FormField`. The host is
responsible for wiring `FormController`'s `errors[name]` into `FormField`'s
`error` prop. `FormController` does NOT read from `FormFieldContext` or write
to it. The two layers are independent and compose orthogonally.

The `required` and `disabled` states on individual fields flow through
`FormFieldContext` (FR-1.4) from the `FormField` props the host declares.
`FormController` does not manage `required` or `disabled` at the field level.

### UC-1.6 Validation lifecycle

Validation runs:

1. **On every value change** (field-level validators only — runs on the
   field that changed). This updates the field's entry in `errors` and
   recomputes form-level `valid`/`canSubmit`.

2. **On submit attempt** (both field-level and form-level `validator`).
   All field-level validators run first; then the form-level `validator`
   runs with the full values map. Form-level errors may override or
   supplement field-level errors.

3. **On `initialValues` change** (all validators re-run against the new
   baseline, same as a value-change sweep).

**Validate-on-blur (deferred — later wave):** Kendo's `validateOn` prop
(which restricts validation to blur events rather than every change) is
deferred from UC-1. The default is validate-on-change per the Kendo minimum
surface. `validateOn` is listed in §UC-1.8 Deferred items.

**ValidationSummary integration:** `FormController` exposes `errors` as a
`Partial<Record<keyof T, string>>` in `renderProps`. Hosts that want a
form-level summary:

```tsx
const errorMessages = Object.entries(renderProps.errors)
  .filter(([, msg]) => Boolean(msg))
  .map(([field, msg]) => ({ field, message: msg! }))

<ValidationSummary errors={errorMessages} />
```

`FormController` does NOT render a `ValidationSummary` automatically.
Visibility, position (above vs below fields), and message formatting are
host decisions.

### UC-1.7 Field integration — FieldRenderProps mapping

When using `FormController`, each field in the form can be managed one of
two ways:

**Option A — Host-threaded (recommended for Harborline forms):**

The host reads values and errors from `renderProps`, threads them into
`FormField` + field input props manually. This is the FR-1.3 composition
approach and requires no new API beyond what UC-1.3 specifies.

```tsx
<FormController initialValues={{ name: '' }} onSubmit={handleSave}>
  {({ errors, getValue, setValue, canSubmit, submit, reset }) => (
    <Form onSubmit={submit}>
      <FormField label="Name" name="name" required error={errors.name}>
        <TextField
          name="name"
          value={getValue('name') as string}
          onChange={v => setValue('name', v)}
          error={Boolean(errors.name)}
        />
      </FormField>
      <div className="flex justify-end gap-2 pt-4">
        <Button variant="ghost" onClick={reset}>Reset</Button>
        <Button type="submit" variant="primary" disabled={!canSubmit}>Save</Button>
      </div>
    </Form>
  )}
</FormController>
```

**Option B — Field-component managed (later wave):** A `<Field>` render-prop
sub-component (Kendo `Field` equivalent) that reads from `FormController`
context and supplies `FieldRenderProps` to its child component. This option
is deferred to UC-2 (see §UC-1.8).

**FieldRenderProps for Option B (deferred shape — informative):**

```typescript
// Deferred — UC-2 wave.
interface FieldRenderProps<V = unknown> {
  value: V
  onChange: (event: { value?: V; target?: unknown }) => void
  onBlur: () => void
  onFocus: () => void
  name: string
  touched: boolean       // this field has been blurred
  modified: boolean      // this field's value differs from initialValues
  valid: boolean         // !errors[name] (FR-1.2: maps from Kendo's valid)
  visited: boolean       // this field has been focused
}
```

Per FR-1.2, there is no `validationMessage` prop on the field component itself.
The `valid` flag maps to `!errors[name]`. The message is surfaced via
`FormField error` prop — the host threads `errors[name]` into `FormField`
(Option A pattern, as shown above).

### UC-1.8 Deferred items

These items are confirmed Kendo-surface capabilities that are deliberately
deferred from UC-1. Each deferred item cites why and when it unblocks.

| Deferred item | Kendo source | Rationale | Unblocks |
|---|---|---|---|
| `validateOn: 'change' \| 'blur' \| 'submit'` | `FormProps.validateOn` | Validate-on-change is the correct default for Harborline data-entry forms (immediate feedback). Blur/submit modes add complexity with marginal benefit for M2. | UC-2 wave |
| `Field` sub-component (Option B field management) | `Field` component | Option A (host-threaded) covers all MVP use cases without additional API surface. `Field` adds render-prop nesting depth. | UC-2 wave |
| `FormContext` (imperative ref handle) | `FormHandle` | No identified MVP use case requiring imperative access outside render-prop scope. | UC-3 wave |
| `FormFieldSet` (grouped fields with fieldset semantics) | `FormFieldSet` component | `FormSection` contract covers the visual grouping use case. ARIA `<fieldset>` semantics are a separate accessibility gap deferred per existing FormSection open questions. | UC-3 wave |
| `onSubmitClick` (fires on every submit-button click regardless of validity) | `FormProps.onSubmitClick` | Niche; covered by Button `onClick` + `canSubmit` guard pattern. | On-demand |
| Per-controller `errors` prop (external override) | `FormProps.errors` | Would require reconciling external and internal error sources. Defer until a real use case (server errors post-submit) drives the design. | On-demand |
