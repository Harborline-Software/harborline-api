# ValidationSummary — Accessibility Contract

- **Component:** ValidationSummary
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationSummary.Semantic.md) · [Interaction](./ValidationSummary.Interaction.md) · [Styling](./ValidationSummary.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationSummary.tsx`
- **Catalog row:** #146 ValidationSummary (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="alert"` | Container `<div>` | Assertive live region — AT announces errors immediately on mount |

---

## 2. Error list

Errors render as `<ul class="list-disc list-inside">` + `<li>` entries. No additional ARIA on list items.

Field-prefixed errors (`ValidationError.field`) render as `"{field}: {message}"` plain text — no explicit field linking (see G-VS1).

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-VS1 | Medium | No linking between error list items and the invalid fields — AT cannot navigate from summary entry to the field | Accepted-risk M1 |
| G-VS2 | Low | `role="alert"` at page load is not announced (live region must already be in DOM before content populates) — only effective when component mounts after submission | Accepted-risk M1 |
