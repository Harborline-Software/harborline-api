# Stepper — Accessibility Contract

- **Component:** Stepper
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Stepper.Semantic.md) · [Interaction](./Stepper.Interaction.md) · [Styling](./Stepper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Stepper.tsx`
- **Catalog rows:** #160 Stepper / #48 Stepper (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `aria-label="Progress steps"` | Container `<ol>` | Hardcoded label |
| `aria-current="step"` | Active step `<li>` | Current step in sequence |
| `aria-hidden="true"` | Check/X SVGs | Decorative status icons |

The container is a native `<ol>` (implicit `role="list"`), and each step is a native `<li>` (implicit `role="listitem"`). **Do NOT use `<div role="list">` / `<div role="listitem">`.** VoiceOver on Safari ignores `role="list"` on non-list elements when CSS `list-style: none` is applied, causing the list semantics to disappear silently. Native `<ol>/<li>` avoids this.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ST1 | Medium | `aria-label="Progress steps"` is hardcoded — cannot be localized or customized | Accepted-risk M1 |
| G-ST2 | Low | No `aria-label` on step indicator circles — number/icon conveys no text meaning to AT | Accepted-risk M1 |
| G-ST3 | Low | Step connector lines have no AT representation — progress between steps is not communicated beyond `aria-current` | Accepted-risk M1 |
