# ValidationSummary — Semantic Contract

- **Component:** ValidationSummary
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ValidationSummary.Interaction.md) · [Accessibility](./ValidationSummary.Accessibility.md) · [Styling](./ValidationSummary.Styling.md)
- **Related contracts:** [FormField.Semantic.md](./FormField.Semantic.md) — per-field error display; [Form.Semantic.md](./Form.Semantic.md) — the host container.
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationSummary.tsx`
- **Catalog row:** #146 ValidationSummary (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled `<ul>` error list

---

## 1. Purpose

ValidationSummary is the canonical **form-level error digest** of
`@harborline-software/ui-react`. It displays a collected list of validation error messages
at the top (or bottom) of a form after submission, giving the user a single
scannable overview of all problems that need to be fixed before the form can
be submitted successfully.

ValidationSummary is **purely presentational** — it does not inspect child
inputs, trigger validation, or integrate with any validation library. The host
computes which errors exist (from react-hook-form, Zod, manual state, or a
server response) and supplies the list; ValidationSummary renders them.

ValidationSummary differs from:

- **FormField `error` prop** — per-field inline error rendered immediately
  below the offending input. Used together with ValidationSummary: the field
  error provides in-place context, the summary provides a top-level overview.
- **StatusBanner** — a full-width banner for non-validation system-level
  feedback (network error, permission error). ValidationSummary is scoped to
  form validation errors.

---

## 2. Data model

ValidationSummary has no internal state.

```typescript
interface ValidationError {
  field?: string    // optional field name; shown as "Name: …" prefix if supplied
  message: string   // the error message text
}

interface ValidationSummaryProps {
  errors: string[] | ValidationError[]
  title?: string
  variant?: 'error' | 'warning'
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `errors` | `string[] \| ValidationError[]` | _required_ | Array of error messages. When empty (`[]`), ValidationSummary renders nothing (null). Hosts must not render the component with a non-empty `errors` array when there are no errors — that would produce an empty error box. |
| `title` | `string` | `'Please fix the following errors'` | Heading text above the error list. Hosts can override for tone ("The following issues were found:", "Unable to save:"). Pass `''` (empty string) to suppress the heading entirely. |
| `variant` | `'error' \| 'warning'` | `'error'` | Color treatment. `'error'` (default) for post-submit validation failure. `'warning'` for pre-submit advisory issues (e.g. "This will affect 3 linked records"). PAO Styling owns the tokens. |

### 3.1 Rendering with `ValidationError` objects vs plain strings

**Plain strings (`errors: string[]`):**
```
• Invoice date is required
• Amount must be greater than zero
• Vendor is required
```

**`ValidationError` objects with `field`:**
```
• Invoice date: Invoice date is required
• Amount: Amount must be greater than zero
• Vendor: Vendor is required
```

The field name is prepended as `"${field}: "` when present. Hosts that use
react-hook-form typically derive plain strings: `Object.values(errors).map(e => e.message)`.
Hosts that want field-name prefixes pass `ValidationError` objects.

### 3.2 Empty state

When `errors` is an empty array (`[]`), ValidationSummary renders **nothing**
(`null`). This makes `errors={errors}` safe to use unconditionally — the
component self-hides when there are no errors. Hosts do not need to
conditionally render the component.

### 3.3 ARIA

ValidationSummary renders a `role="alert"` region (or `aria-live="assertive"`)
so that AT announces the error list when it appears after form submission.
PAO Accessibility owns the exact ARIA pattern.

### 3.4 HTML attribute passthrough

ValidationSummary spreads HTML attributes onto the root element. Includes
`id` (useful for `aria-describedby` on the parent form) and `className`.

---

## 4. Events — semantics

ValidationSummary has **no events**. It is presentational.

---

## 5. Slots

ValidationSummary has no slot extensibility in M1. The error list is
`string[]`/`ValidationError[]` only — no ReactNode per-error rendering.

Rich per-error content (e.g. a "Go to field" link per error item) is deferred
(§7).

---

## 6. Component composition

### Post-submit form errors (typical usage)

```tsx
const { formState: { errors }, handleSubmit } = useForm<FormData>({
  resolver: zodResolver(schema)
})

// Flatten react-hook-form errors to string array:
const errorMessages = Object.entries(errors).map(
  ([field, err]) => `${fieldLabels[field]}: ${err?.message ?? 'Invalid'}`
)

<Form onSubmit={handleSubmit(onValid)}>
  {errorMessages.length > 0 && (
    <ValidationSummary errors={errorMessages} />
  )}
  <FormField label="Invoice date" name="invoiceDate" error={errors.invoiceDate?.message}>
    <DateField name="invoiceDate" … />
  </FormField>
  …
</Form>
```

### Server-returned errors

After a form submits to the server, the server may return validation errors
beyond what client-side validation catches:

```tsx
const [serverErrors, setServerErrors] = useState<string[]>([])

const onSubmit = async (data: FormData) => {
  const result = await saveInvoice(data)
  if (!result.ok) {
    setServerErrors(result.errors.map(e => e.message))
    return
  }
  onSuccess()
}

<ValidationSummary errors={serverErrors} title="Unable to save invoice" />
```

### Advisory warnings (pre-submit)

```tsx
<ValidationSummary
  errors={['Changing the vendor will recalculate all line items.']}
  variant="warning"
  title="Please review before saving"
/>
```

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Rich per-error content** — ReactNode per error entry (e.g. a "Jump to
  field" anchor link). Today the list is text-only. A future `errorRenderer`
  prop may provide a render function `(error) => ReactNode`.
- **Animated enter** — the summary appearing with a slide-in animation after
  form submission. Deferred to PAO Styling wave.
- **Field-link integration** — clicking an error jumps focus to the
  corresponding input. Would require a field-id-resolution mechanism not
  present in M1.
- **Max-error truncation** — showing only the first N errors with a "and N
  more" collapse. Hosts can truncate the `errors` array themselves today.
- **Dismissible** — a close button to hide the summary. Hosts control
  visibility via the `errors` array contents.

---

## 8. Open questions (for council)

1. **`role="alert"` vs `role="region"`.** `role="alert"` announces the
   content immediately to AT when the component mounts/updates; `role="region"`
   with `aria-live="polite"` is less aggressive. For form submission errors,
   `alert` (assertive) is the typical choice — confirm with PAO Accessibility.
2. **`title` default text.** The default "Please fix the following errors" is
   common but may feel prescriptive. Should the default be empty (no heading)
   and force hosts to supply it? Leaning keep default — most hosts don't
   customize.
3. **Field labels in `ValidationError`.** The `field` key is the programmatic
   field name (e.g. `invoiceDate`); hosts must map it to a human-readable
   label. Should the contract provide a `fieldLabels?: Record<string, string>`
   prop to perform this mapping internally? Leaning no — keep the mapping
   host-owned to avoid coupling ValidationSummary to the form's field
   structure.
4. **`warning` variant use case.** `variant="warning"` for pre-submit
   advisories is niche and may be confusing alongside FormField `error`
   prop. Should M1 be error-only? Leaning keep `warning` — the use case
   appears in bulk-edit and destructive-action forms.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |

---

## Wave UC-1 — FormController integration note

_Cross-reference: Form.Semantic §UC-1.6 (validation lifecycle) + §UC-1.7 (Option A field integration)._
_Status: Draft._

### UC-1.1 Consuming FormController errors in ValidationSummary

`FormController` surfaces errors as `Partial<Record<keyof T, string>>` in
`renderProps.errors`. The host flattens to `ValidationError[]` for
`ValidationSummary`:

```tsx
const summaryErrors = Object.entries(renderProps.errors)
  .filter((entry): entry is [string, string] => Boolean(entry[1]))
  .map(([field, message]) => ({ field, message }))

<ValidationSummary errors={summaryErrors} />
```

`ValidationSummary` self-hides when `errors` is empty (§3.2 above), so
this is safe to render unconditionally.

### UC-1.2 Display timing

`ValidationSummary` SHOULD be rendered only after a submit attempt (when
`renderProps.submitted` is `false` but a submit was triggered and failed).
Rendering it before the first submit attempt exposes an empty summary or
pre-emptive errors, which degrades the UX. The host controls this timing
by gating on `renderProps.touched` or a local `hasAttemptedSubmit` flag.

### UC-1.3 Mapping Kendo `validationSummary` prop

Kendo's `Form` component has a `validationSummary` prop that renders the
built-in summary automatically. This library does NOT have an equivalent.
Per FR-1.3 (family-rulings-2026-06-11.md), validation messages stay in the
composition layer: the host explicitly places `ValidationSummary` as a
peer child of the `Form` layout container. Auditors should NOT flag the
absence of a `validationSummary` prop on `FormController` as a Kendo gap.
