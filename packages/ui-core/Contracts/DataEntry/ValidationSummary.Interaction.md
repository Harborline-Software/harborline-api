# ValidationSummary — Interaction Contract

- **Component:** ValidationSummary
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationSummary.Semantic.md) · [Accessibility](./ValidationSummary.Accessibility.md) · [Styling](./ValidationSummary.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationSummary.tsx`
- **Catalog row:** #146 ValidationSummary (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Interaction model

ValidationSummary is a **read-only display component**. It has no interactive elements.

---

## 2. Conditional rendering

`errors.length === 0` → returns `null`. The component is not mounted when there are no errors.

---

## 3. Known gaps

None. ValidationSummary is intentionally non-interactive — all error-field linking is the caller's responsibility.
