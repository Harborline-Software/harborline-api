# FormController — Styling Contract

- **Component:** FormController
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Draft
- **Companion contracts:** [Semantic](./FormController.Semantic.md) · [Interaction](./FormController.Interaction.md) · [Accessibility](./FormController.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormController.tsx`

---

## 1. Purpose

`FormController` is a **headless** render-prop controller (UC-1). It renders no
DOM of its own — it returns a fragment around its `render`/`children` function's
output and owns only values, validation, and submission eligibility. It
therefore has **no token surface and no visual footprint**. This contract pins
that boundary and points at where the visuals actually live (the host-composed
`Form` / `FormField` / `Button`), staying consistent with the family ruling
already recorded in [Form.Styling — Wave UC-1](./Form.Styling.md).

---

## 2. Token surface

**None.** `FormController` declares no `--sf-formcontroller-*` tokens.

| Token | Semantic role | Notes |
|---|---|---|
| — | — | No tokens — the controller paints nothing. Adding a `--sf-formcontroller-*` paint token here would be a contract violation |

The controller's derived flags (`canSubmit`, `valid`, `modified`, `touched`,
`visited`, `submitted`, `errors`) are **state inputs to host styling decisions**,
not styles themselves.

---

## 3. Host-composed styling

All visual output comes from the function passed to `render`/`children`. The
host SHOULD compose:

| Concern | Owner | Token source |
|---|---|---|
| Form layout + field gap | `Form` layout container inside the render prop | [Form.Styling §1–§4](./Form.Styling.md) (`data-layout`, flex-column `gap`) |
| Per-field chrome (label / hint / error palette) | `FormField` (host threads `error` / `disabled` from `renderProps`) | [FormField.Styling](./FormField.Styling.md) |
| Submit gating | host binds `!canSubmit` → submit `Button` `disabled` | [Button.Styling](./Button.Styling.md) disabled palette |

`FormController` owns no additional styling for any of these — it supplies the
state, the composed components supply the paint (mirrors
[Form.Styling UC-1.1–UC-1.3](./Form.Styling.md)).

---

## 4. Visual state inventory

`FormController` has **no visual states** — it has no DOM to put a state on.

| State | Trigger | Recipe |
|---|---|---|
| (none) | — | The controller is headless; `valid` / `modified` / `submitted` / `errors` drive **host** styling, not controller styling |

---

## 5. ValidationSummary placement

When `errors` is non-empty after a submit attempt, the host places a
`ValidationSummary` at the **top** of the form. The gap between the summary and
the first `FormField` SHOULD use the `Form` layout container's `gap` token — the
summary is a peer child of `Form` and inherits the flex-column gap automatically
(per [Form.Styling UC-1.4](./Form.Styling.md)). `FormController` contributes no
placement token of its own.

---

## 6. Responsive, RTL, reduced motion

Not applicable at the controller level — the controller renders nothing.
Responsive reflow, RTL logical-property handling, and reduced-motion are all
properties of the host-composed `Form` / `FormField` / inputs.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
