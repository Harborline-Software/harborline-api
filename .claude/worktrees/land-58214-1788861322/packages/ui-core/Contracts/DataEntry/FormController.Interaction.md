# FormController — Interaction Contract

- **Component:** FormController
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Draft
- **Companion contracts:** [Semantic](./FormController.Semantic.md) · [Styling](./FormController.Styling.md) · [Accessibility](./FormController.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormController.tsx`

---

## 1. Scope

This contract governs the controller's **state machine**: how values change,
when validators run, how the derived flags are computed, and what gates
submission. `FormController` is headless — it adds no DOM, focus, or keyboard
handling of its own; those belong to the host-composed inputs and submit
control. The behaviours here are observable through the render-props object.

## 2. Activation & state

Derived flags (recomputed on every relevant change):

- **`modified`** — any value differs from the current `initialValues` snapshot
  (including keys added beyond the snapshot).
- **`valid`** — every active validator passes (the `errors` map has no truthy
  entries).
- **`canSubmit`** — `valid && (modified || ignoreModified)`.
- **`visited` / `touched`** — aggregate flags: true once at least one field has
  been focus-signalled (`markVisited`) / blur-signalled (`markTouched`).
- **`submitted`** — true after a successful `onSubmit`.

Transitions:

- **`setValue(name, value)`** → updates the value, runs that field's validator
  (only), recomputes `errors`/`valid`/`modified`, then fires `onChange` with the
  post-update getter.
- **`submit(event?)`** → runs all field validators, then the form-level
  `validator`; **form-level errors override field-level**; if the merged result
  is valid and submittable, sets `submitted` and calls `onSubmit`.
- **`reset()`** → restores `initialValues`, clears `visited`/`touched`/
  `submitted`/`errors`, and re-runs field validators against the baseline.
- **`initialValues` identity change** → recomputes `modified` and re-runs
  validators against the new baseline.

## 3. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab | Not handled by the controller — tab order is owned by the host-composed inputs. |
| Enter / Space | Not handled directly. A host typically wires a submit button or `<form onSubmit>` to `submit()`, which guards `canSubmit`. |

## 4. Disabled handling

The controller has no `disabled` prop. Hosts derive control disabled-ness from
the render props — e.g. disable the submit button when `!canSubmit`, or disable
fields while `submitted`. The controller never disables anything itself.

## 5. Interaction-state precedence

Submission-eligibility precedence (not a visual precedence):

1. **Form-level errors override field-level errors** on submit.
2. **`canSubmit` requires `valid`** — an invalid form never submits, regardless
   of `modified`.
3. **`modified` is required unless `ignoreModified`** — an unchanged valid form
   submits only when `ignoreModified` is set.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
