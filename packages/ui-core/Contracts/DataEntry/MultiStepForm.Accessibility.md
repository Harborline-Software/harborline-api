# MultiStepForm — Accessibility Contract

- **Component:** MultiStepForm
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiStepForm.Semantic.md) · [Interaction](./MultiStepForm.Interaction.md) · [Styling](./MultiStepForm.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiStepForm.tsx`
- **Catalog row:** (not-in-catalog) MultiStepForm (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<nav aria-label="Form progress">` | Step nav | Navigation landmark with label |
| `<ol>` | Steps list | Ordered list |
| `<button type="button">` | Each step | Keyboard accessible |
| `aria-current="step"` | Current step button | Identifies current step to AT |
| `disabled` | Non-navigable step | Native HTML disabled |
| `aria-hidden="true"` | Connector line | Decorative element hidden from AT |
| `aria-hidden="true"` | Checkmark SVG | Decorative icon hidden from AT |

---

## 2. Checkmark accessibility

The checkmark SVG has `aria-hidden="true"`. Completed steps have no text fallback for AT — a screen reader hears only the step number (rendered as the button's text content before the complete state). The visual check is not announced.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MSF2 | Medium | Complete step shows checkmark SVG but step number text is replaced — AT loses number context; no `aria-label="Step N, complete"` | Accepted-risk M1 |
| G-MSF3 | Low | Step description (if provided) is not linked to the button via `aria-describedby` | Accepted-risk M1 |
