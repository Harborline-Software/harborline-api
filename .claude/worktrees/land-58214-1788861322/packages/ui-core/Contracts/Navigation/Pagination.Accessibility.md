# Pagination — Accessibility Contract

- **Component:** Pagination
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Pagination.Semantic.md) · [Interaction](./Pagination.Interaction.md) · [Styling](./Pagination.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Pagination.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="navigation"` | Root `<nav>` | Navigation landmark |
| `aria-label="Pagination"` | Root `<nav>` | Named landmark |
| `aria-label="Go to previous page"` | Prev button | Accessible label |
| `aria-label="Go to next page"` | Next button | Accessible label |
| `aria-label="Go to first page"` | First button (`showEdges`) | Accessible label |
| `aria-label="Go to last page"` | Last button (`showEdges`) | Accessible label |
| `aria-label="Page N"` | Numbered page button | `"Page {n}"` |
| `aria-current="page"` | Active page button | Current page marker |
| `disabled` | Prev/next/edge when at limit | HTML disabled |
| `aria-hidden="true"` | DOTS `<span>` | Decorative ellipsis |
| `aria-hidden="true"` | All navigation SVG icons | Decorative icons |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PAGIN6 | Low | `aria-label="Pagination"` hardcoded — not localizable | Accepted-risk M1 |
