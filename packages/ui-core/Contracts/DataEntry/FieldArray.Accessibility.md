# FieldArray — Accessibility Contract

- **Component:** FieldArray
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FieldArray.Semantic.md) · [Interaction](./FieldArray.Interaction.md) · [Styling](./FieldArray.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FieldArray.tsx`
- **Catalog row:** #54 FieldArray (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

`FieldArray` renders no DOM elements and has no ARIA attributes. Accessibility is entirely the caller's responsibility.

---

## 2. Caller obligations

The caller (the `children` render function) must:
- Provide accessible labels for each field row
- Announce when fields are added/removed (e.g., via `aria-live` region)
- Ensure remove/move buttons have accessible names (e.g., `aria-label="Remove item 2"`)

---

## 3. Known gaps

None at the component level — FieldArray is headless and delegates all DOM to the caller.
