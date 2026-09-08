# Label — Accessibility Contract

- **Component:** Label
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Label.Semantic.md) · [Interaction](./Label.Interaction.md) · [Styling](./Label.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no direct implementation)
- **Catalog row:** #74 Label (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix Label baseline)

---

## 1. Native `<label>` semantics

Uses native `<label>` element with `for` attribute (`htmlFor` in React). Screen readers associate the label text with the control and announce it when the control receives focus.

---

## 2. Required field indicator

When a field is required, the label text should include a visual indicator (e.g., asterisk `*`). The asterisk must also be announced to screen readers — use `aria-label` on the indicator or include text like `(required)` in a screen-reader-only span.

---

## 3. Disabled state

`peer-disabled:opacity-70` visually dims the label when the associated input is disabled. Screen readers announce disabled state from the input, not the label.

---

## 4. Known gaps

None identified.
