# FormController — Accessibility Contract

- **Component:** FormController
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Draft
- **Companion contracts:** [Semantic](./FormController.Semantic.md) · [Interaction](./FormController.Interaction.md) · [Styling](./FormController.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormController.tsx`

---

## 1. Purpose

`FormController` is a **headless** controller that emits no DOM and therefore no
ARIA. Its accessibility contribution is indirect but real: the `errors` map and
the derived flags (`canSubmit`, `valid`, `submitted`, …) it surfaces through
`renderProps` are what the host wires into accessible patterns — error
announcement, per-field error association, and submit-gating. This contract
formalises, for the standalone `FormController` component, the same model
already ruled for the family in
[Form.Accessibility — Wave UC-1](./Form.Accessibility.md) (ruling FR-1).

---

## 2. Roles & ARIA wiring

| Element | Role / attribute | Notes |
|---|---|---|
| `FormController` output | **NONE** | The controller returns `<>{content}</>` — a fragment. It adds no role, no `aria-*`, no landmark |
| Form landmark, fields, summary | host-composed | The accessible structure lives in the host's `<form>` / `Form` + `FormField` + `ValidationSummary`, not the controller |

The controller MUST NOT inject ARIA — doing so would attach attributes to an
element it does not own.

**WCAG citation:** SC 4.1.2 Name, Role, Value — discharged by the host-composed
elements; the controller is a behavioural seam only.

---

## 3. Error announcement (host pattern)

On a failed submit attempt (`errors` non-empty after `submit()`), the host
SHOULD place a `ValidationSummary` at the top of the form in a focusable
container and move focus to it so AT announces the error list:

```tsx
const summaryRef = useRef<HTMLDivElement>(null)
useEffect(() => {
  if (Object.keys(renderProps.errors).length > 0) summaryRef.current?.focus()
}, [renderProps.errors])

<div ref={summaryRef} tabIndex={-1}>
  <ValidationSummary errors={errorMessages} />
</div>
```

This is the WCAG error-focus pattern; PAO Accessibility owns this focus-
management detail (per [Form.Accessibility UC-1.1](./Form.Accessibility.md)).

**WCAG citations:** SC 3.3.1 Error Identification; SC 3.3.3 Error Suggestion.

---

## 4. Per-field error wiring

`FormController` `errors` flow through `FormField`'s `error` prop, which renders
the message with `role="alert"` and already includes the error node id in the
field's `aria-describedby` (per [FormField.Accessibility](./FormField.Accessibility.md)).
No additional ARIA wiring is needed at the `FormController` level (per
[Form.Accessibility UC-1.2](./Form.Accessibility.md)).

**WCAG citation:** SC 4.1.3 Status Messages — the `role="alert"` error node is
the live channel.

---

## 5. Required & disabled state

`FormController` manages **neither** `required` nor `disabled` at the field
level. These flow through `FormField` props → `FormFieldContext` → child inputs
(FR-1.4). A host that disables a field based on form state derives the prop from
`renderProps` and threads it into `FormField disabled` (per
[Form.Accessibility UC-1.3](./Form.Accessibility.md)):

```tsx
<FormField label="End date" name="endDate" disabled={getValue('startDate') === null}>
  <DateField … />
</FormField>
```

---

## 6. Keyboard & focus

The controller binds no keys and owns no focus. Tab order is owned by the
host-composed inputs; `submit()` (wired by the host to a submit `Button` or
`<form onSubmit>`) guards `canSubmit` before invoking `onSubmit` (per
[Interaction §3](./FormController.Interaction.md)). The controller never moves
focus itself — the post-submit focus move to the `ValidationSummary` (§3) is the
host's responsibility.

**WCAG citation:** SC 2.1.1 Keyboard — the controller contributes no keyboard
surface.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FC3 | Medium | No built-in focus-move to `ValidationSummary` on submit failure — it is the host's responsibility | Accepted-risk UC-1; host pattern documented in §3 (mirrors [Form.Accessibility UC-1.4](./Form.Accessibility.md)) |
| G-FC4 | Low | When `canSubmit` transitions `false → true` (submit unblocked), no `aria-live` announcement is emitted | Deferred — no identified AT use case |

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
