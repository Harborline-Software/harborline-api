# Wizard — Accessibility Contract

- **Component:** Wizard
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Wizard.Semantic.md) · [Interaction](./Wizard.Interaction.md) · [Styling](./Wizard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Wizard.tsx`
- **Catalog row:** #150 Wizard (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Step indicator ARIA

The step progress indicator uses `<nav aria-label="Wizard steps">`:

| Attribute | Element | Value |
|---|---|---|
| `<nav>` | Step indicator wrapper | Navigation landmark |
| `aria-label="Wizard steps"` | `<nav>` | Names the step-tracker navigation |
| `<ol>` | Step list | Ordered list conveys step sequence |
| `aria-current="step"` | Active step circle | Marks the current step for AT |
| `aria-hidden="true"` | Checkmark SVG in completed steps | Decorative icon |

---

## 2. Content area

The step content renders in a plain `<div>`. No special ARIA role is required —
the content is whatever the host provides. If step transitions should be announced
to AT, the host adds `aria-live="polite"` to the content container.

---

## 3. Navigation buttons

Back and Next buttons are `<button>` elements:
- `disabled` attribute applied when the button is not active.
- AT announces `"Next, button"` or `"Finish, button"` per `completeLabel`.
- No `aria-label` override needed — button text is descriptive.

---

## 4. Focus management on step transition

When `currentIndex` changes (Next/Back), focus is not automatically managed —
it remains on the clicked button. The new step content appears above the buttons;
screen reader users Tab through it naturally.

For strict WCAG compliance, focus SHOULD be moved to the new step heading or
the start of the content area on step transition. This is a host responsibility
in M1.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-WZ3 | Medium | No automatic focus management on step change | Accepted-risk M1; host adds focus management if needed |
| G-WZ4 | Low | No live region for step change announcement | Accepted-risk M1; `aria-current="step"` updates on re-render |
