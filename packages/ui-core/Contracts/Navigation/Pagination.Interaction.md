# Pagination — Interaction Contract

- **Component:** Pagination
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Pagination.Semantic.md) · [Accessibility](./Pagination.Accessibility.md) · [Styling](./Pagination.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Pagination.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Button actions

| Button | Condition | Effect |
|---|---|---|
| Previous | `page > 1` | `onPageChange(page - 1)` |
| Previous | `page === 1` | Disabled (no-op) |
| Next | `page < totalPages` | `onPageChange(page + 1)` |
| Next | `page === totalPages` | Disabled (no-op) |
| First (showEdges) | `page > 1` | `onPageChange(1)` |
| First (showEdges) | `page === 1` | Disabled |
| Last (showEdges) | `page < totalPages` | `onPageChange(totalPages)` |
| Last (showEdges) | `page === totalPages` | Disabled |
| Page button | Any | `onPageChange(p)` |

DOTS ellipsis (`...`) has no click handler.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PAGIN5 | Low | No keyboard shortcut beyond Tab navigation between buttons | Accepted-risk M1 |
