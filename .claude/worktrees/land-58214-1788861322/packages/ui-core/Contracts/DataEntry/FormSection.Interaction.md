# FormSection — Interaction Contract

- **Component:** FormSection
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FormSection.Semantic.md) · [Interaction](./FormSection.Interaction.md) · [Accessibility](./FormSection.Accessibility.md) · [Styling](./FormSection.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormSection.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

FormSection is a purely structural layout component. It has no interactive
behaviour — no expand/collapse, no click handlers, no form-level events. This
contract is brief by design.

---

## 2. Behaviour

FormSection renders its children in a CSS grid and wraps them in a `<fieldset>`.
It has no internal state and no event handlers. All interaction occurs within
the child components.

---

## 3. Tab order

FormSection does not modify the Tab order. Focus flows through children in DOM
order within the `<fieldset>`.

---

## 4. No known interaction gaps

FormSection has no interaction gaps — it is intentionally static.
