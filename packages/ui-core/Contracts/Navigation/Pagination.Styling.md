# Pagination — Styling Contract

- **Component:** Pagination
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Pagination.Semantic.md) · [Interaction](./Pagination.Interaction.md) · [Accessibility](./Pagination.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Pagination.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Nav container

`flex items-center gap-1` + `className` passthrough.

---

## 2. Page button (base)

`inline-flex h-9 min-w-[2.25rem] items-center justify-center rounded-md px-3 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500`

> **M1 note:** Colors are hardcoded palette classes, not design tokens.

---

## 3. Page button states

| State | Additional classes |
|---|---|
| Active (current page) | `bg-blue-600 text-white hover:bg-blue-700` |
| Inactive numbered page | `text-gray-700 hover:bg-gray-100` |
| Prev/Next/Edge (enabled) | `text-gray-500 hover:bg-gray-100` |
| Disabled | `disabled:pointer-events-none disabled:opacity-40` |

---

## 4. DOTS ellipsis

`inline-flex h-9 w-9 items-center justify-center text-sm text-gray-400`

Not interactive (`aria-hidden="true"`).

---

## 5. Icons

`h-4 w-4` — prev/next/edge navigation SVGs.
