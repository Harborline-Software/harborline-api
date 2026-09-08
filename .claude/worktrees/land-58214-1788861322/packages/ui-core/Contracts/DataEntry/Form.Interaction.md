# Form — Interaction Contract

- **Component:** Form
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Form.Semantic.md) · [Accessibility](./Form.Accessibility.md) · [Styling](./Form.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Form.tsx`
- **Catalog row:** #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Submit behavior

When `onSubmit` is provided, the component attaches a handler that calls `e.preventDefault()` then fires `onSubmit(e)`.

When `onSubmit` is not provided, no `onSubmit` attribute is attached to the `<form>` — native browser form submission proceeds.

---

## 2. Layout behavior

Form is a layout primitive. It arranges its children in a flex column with configurable gap. No built-in field validation, state management, or step logic.

---

## 3. Known gaps

None. Form intentionally delegates all validation and state to the caller.

---

## Wave UC-1 — FormController interaction model

_Ruling: FR-1 (family-rulings-2026-06-11.md). Kendo minimum surface per 2026-06-12 re-audit._
_Status: Draft._

### UC-1.1 Validation trigger points

`FormController` (Form.Semantic §UC-1.6) runs validators at three trigger
points:

1. **Field value changes** — field-level validators run for the changed field
   immediately. The form-level `validator` does NOT run on every keystroke.
2. **Submit attempt** — all field-level validators run for every field first,
   then the form-level `validator` runs with the full values snapshot.
3. **`initialValues` prop change** — all validators re-run against the new
   baseline.

### UC-1.2 `canSubmit` gate behavior

The submit button SHOULD be bound to `renderProps.canSubmit` (mapped from
Kendo `FormRenderProps.allowSubmit`). When `canSubmit` is `false`:

- The host SHOULD disable the submit button (`disabled={!canSubmit}`).
- If the user triggers a submit another way (Enter key in a text field),
  `FormController` re-validates and does NOT call the `onSubmit` callback
  when `canSubmit` is `false`. The host's `onSubmit` is never called in an
  invalid or unmodified state (unless `ignoreModified=true`).

### UC-1.3 `reset()` interaction model

`reset()` is synchronous. After calling `reset()`:

- All field values revert to the current `initialValues` snapshot.
- `visited`, `touched`, `modified`, `submitted` all become `false`.
- Validators re-run against the initial values; `valid` and `errors` update.
- `canSubmit` becomes `false` (since `modified` is `false`) unless
  `ignoreModified` is `true`.

### UC-1.4 `onChange` observer

`FormController`'s `onChange` prop is an observer, NOT a controller. It fires
AFTER internal state has updated. Use a field-level `validator` for value
rejection logic. The observer receives `(name, value, getValue)` where
`getValue` returns the post-update state.

### UC-1.5 `markVisited` / `markTouched` — focus/blur signal wiring

The `FormController` render-props include two focus/blur signal functions:

- `markVisited(name: keyof T)` — call from the input's `onFocus` handler.
  Sets the per-field visited flag; once set, the form-level `visited`
  aggregate becomes `true`.
- `markTouched(name: keyof T)` — call from the input's `onBlur` handler.
  Sets the per-field touched flag; once set, the form-level `touched`
  aggregate becomes `true`.

Both calls are idempotent: a second `markVisited('email')` call for the same
field is a no-op (the internal Set already contains the name) and causes no
re-render. This means it is safe to call them from every `onFocus`/`onBlur`
event without guarding for duplication.

These signals exist because `FormController` has no direct access to the host's
input DOM events — it is a headless controller that never renders inputs
itself. The Option A host-threading pattern requires the host to forward these
signals explicitly. When UC-2 `Field` ships it will forward them automatically;
until then, Option A hosts are responsible for the wiring.

### UC-1.7 Known gaps

| Gap ID | Description | Disposition |
|---|---|---|
| G-FC1 | `validateOn` prop — deferred validation to blur or submit trigger not implemented in UC-1. | Deferred — UC-2 wave per Form.Semantic §UC-1.8 |
| G-FC2 | No imperative `FormRef` handle — cannot trigger `submit()` or `reset()` from outside the render-prop scope without lifting state. | Deferred — UC-3 wave |
