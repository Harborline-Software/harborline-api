# SmartPasteButton — Accessibility Contract

- **Component:** SmartPasteButton
- **ADR 0017 family:** AI
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./SmartPasteButton.Semantic.md) · [Interaction](./SmartPasteButton.Interaction.md) · [Styling](./SmartPasteButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A28 SmartPasteButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SmartPasteButton baseline)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<button type="button">` | Root | Native button; keyboard focusable |
| `aria-disabled={disabled}` | Root | When `disabled=true` |
| `aria-busy={loading}` | Root | `true` during loading state — AT announces "busy" |
| `aria-label` | Root | Consumer may supply explicit label; default is button text content |

---

## 2. Loading state announcement

When `loading=true`, `aria-busy="true"` on the button signals to AT that an operation is in progress. Because the button is still in the DOM (not replaced), AT retains focus context. Callers should also provide a live region (e.g. `role="status"`) outside the button to announce completion or error.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SPB-A1 | Medium | No built-in `aria-live` region for paste result or error — AT feedback on success/failure is caller responsibility | Accepted-risk M1; consistent with SmartPasteButton's stateless design |
