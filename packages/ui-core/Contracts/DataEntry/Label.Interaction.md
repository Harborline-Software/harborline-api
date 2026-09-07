# Label — Interaction Contract

- **Component:** Label
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Label.Semantic.md) · [Accessibility](./Label.Accessibility.md) · [Styling](./Label.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no direct implementation)
- **Catalog row:** #74 Label (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix Label baseline)

---

## 1. Click behavior

Clicking the label focuses and activates the associated control (`htmlFor` target). Native `<label>` browser behavior.

---

## 2. Radix `onMouseDown` guard

Radix Label overrides `onMouseDown` to prevent focus loss when clicking interactive children inside the label (e.g., a link inside a label text). This prevents the browser's native "click label → mousedown → focus associated input" from firing incorrectly in nested-interactive cases.

---

## 3. No stateful interaction

Label has no internal state. All visual changes on interaction are CSS-driven (peer selectors).

---

## 4. Known gaps

None identified.
