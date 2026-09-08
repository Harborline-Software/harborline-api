# Pagination — Semantic Contract

- **Component:** Pagination
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Pagination.Interaction.md) · [Accessibility](./Pagination.Accessibility.md) · [Styling](./Pagination.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Pagination.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled button-based navigator

---

## 1. Component purpose

**Pagination** — page navigation control with prev/next buttons, numbered page buttons, ellipsis gaps, and optional first/last edge buttons.

---

## 2. Props

```typescript
interface PaginationProps extends React.HTMLAttributes<HTMLElement> {
  page: number            // 1-based current page
  totalPages: number
  onPageChange: (page: number) => void
  siblingCount?: number   // default: 1; pages shown on each side of current
  showEdges?: boolean     // default: false; show first/last buttons
}
```

---

## 3. Page range algorithm

Total slots = `siblingCount * 2 + 5`. If total slots ≥ totalPages, show all pages.

Otherwise:
- Always show page 1 and `totalPages`
- Show `siblingCount` siblings on each side of `page`
- Replace gaps > 1 with `...` ellipsis

---

## 4. Events

`onPageChange(page: number)` — fires on every button click. Callers are responsible for clamping to `[1, totalPages]` — the component uses `disabled` to prevent out-of-bounds navigation.
