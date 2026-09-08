# Input — Accessibility Contract

- **Component:** Input
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Input.Semantic.md) · [Interaction](./Input.Interaction.md) · [Styling](./Input.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Input.tsx`
- **Catalog row:** #72 Input (`app-priority: critical`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

The `<input>` element has implicit `role="textbox"` (for `type="text"`) or type-appropriate role from the browser. No additional ARIA is added by the component.

`invalid=true` → `border-destructive` class only — no `aria-invalid` attribute is set. Callers must add `aria-invalid` explicitly.

---

## 2. Prefix/suffix wrapper

The prefix/suffix `<div>` wrapper adds `focus-within:ring-2` — keyboard focus ring appears on the container, not the inner `<input>`.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-IN1 | High | `invalid=true` applies visual styling but does NOT set `aria-invalid` — AT will not announce the invalid state | Accepted-risk M1; higher-level wrappers (TextField, TextAreaField) add `aria-invalid` |
| G-IN2 | Low | Prefix/suffix `<span>` elements are not announced by AT (no `aria-label` or role) | Accepted-risk M1 |
