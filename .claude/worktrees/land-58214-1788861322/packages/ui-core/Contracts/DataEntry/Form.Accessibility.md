# Form — Accessibility Contract

- **Component:** Form
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Form.Semantic.md) · [Interaction](./Form.Interaction.md) · [Styling](./Form.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Form.tsx`
- **Catalog row:** #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. ARIA structure

Form renders a native `<form>` element. No ARIA roles are added — `<form>` has the implicit `role="form"` when it has an accessible name (`aria-label` or `aria-labelledby`); otherwise it is anonymous.

Callers may add `aria-label` or `aria-labelledby` via the `...props` passthrough to make the form a named landmark.

---

## 2. Layout mode

`data-layout={layout}` is a data attribute for styling/CSS purposes only — not ARIA-relevant.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FM1 | Low | No `aria-label` by default — form is not a named landmark; screen reader navigation by landmark won't surface it | Accepted-risk M1; callers add via props passthrough |

---

## Wave UC-1 — FormController accessibility model

_Ruling: FR-1 (family-rulings-2026-06-11.md). Kendo minimum surface per 2026-06-12 re-audit._
_Status: Draft._

### UC-1.1 Error announcement — ValidationSummary placement

When `FormController` exposes `errors` in `renderProps`, the host SHOULD place
a `ValidationSummary` at the TOP of the form (above the first `FormField`) and
wire it to a stable `id`. After a failed submit attempt, the host SHOULD move
focus to the `ValidationSummary` container so AT announces the error list:

```tsx
const summaryRef = useRef<HTMLDivElement>(null)

// On submit failure (canSubmit stays false after submit attempt):
useEffect(() => {
  if (Object.keys(renderProps.errors).length > 0) {
    summaryRef.current?.focus()
  }
}, [renderProps.errors])

<div ref={summaryRef} tabIndex={-1}>
  <ValidationSummary errors={errorMessages} />
</div>
```

This matches the WCAG 2.2 AA error focus pattern and Kendo's recommended
`ValidationSummary` integration. PAO Accessibility owns the focus management
detail.

### UC-1.2 Per-field error wiring

`FormController` errors flow through `FormField`'s `error` prop, which renders
with `role="alert"` per FormField.Accessibility. Individual field `aria-describedby`
already includes the error node id when `FormField error` is supplied — no
additional ARIA wiring is needed at the `FormController` level.

### UC-1.3 Required and disabled state

`FormController` does NOT manage `required` or `disabled` at the field level.
These flow through `FormField` props → `FormFieldContext` → child inputs per
FR-1.4 (FormField.Semantic §Wave FR-1.4). `FormController` hosts that want
to disable a field based on form state must derive the prop from `renderProps`
and pass it through `FormField disabled`:

```tsx
<FormField label="End date" name="endDate"
  disabled={getValue('startDate') === null}
>
  <DateField … />
</FormField>
```

### UC-1.4 Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FC3 | Medium | No built-in focus-move to ValidationSummary on submit failure — host responsibility | Accepted-risk UC-1; host pattern documented in §UC-1.1 |
| G-FC4 | Low | `canSubmit=false` does not emit an `aria-live` announcement when it transitions to true (submit unblocked) | Deferred — no identified AT use case |
