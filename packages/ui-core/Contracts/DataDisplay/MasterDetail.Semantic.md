# MasterDetail — Semantic Contract

- **Component:** MasterDetail
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MasterDetail.Interaction.md) · [Accessibility](./MasterDetail.Accessibility.md) · [Styling](./MasterDetail.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Vaadin Details / expand-in-place pattern)
- **Catalog row:** #A12 MasterDetail (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Vaadin Details baseline)

---

## 1. Component purpose

**MasterDetail** — a two-pane layout where a "master" list and a "detail" panel are shown side by side. Selecting an item in the master list updates the detail panel. Can also operate as an expand-in-place pattern (DataGrid row expansion) depending on layout mode.

---

## 2. Props (planned)

```typescript
interface MasterDetailProps<T = unknown> {
  items: T[]
  selectedKey?: string | number
  defaultSelectedKey?: string | number
  onSelectionChange?: (key: string | number | null, item: T | null) => void
  masterRender: (item: T, selected: boolean) => React.ReactNode
  detailRender: (item: T | null) => React.ReactNode
  emptyDetailContent?: React.ReactNode   // shown when nothing is selected
  layout?: 'side-by-side' | 'stacked'   // default: 'side-by-side'
  masterWidth?: string | number          // default: '40%'
  className?: string
}
```

---

## 3. Layout modes

**side-by-side** (default): master list on the left, detail panel on the right. Fixed proportional split.

**stacked**: master above detail. Used for narrow viewports or mobile-first layouts.

---

## 4. Empty state

When no item is selected, `detailRender(null)` is called. Callers use `emptyDetailContent` or return an empty state from `detailRender`.

---

## 5. Controlled / uncontrolled

Controlled with `selectedKey` + `onSelectionChange`. Uncontrolled with `defaultSelectedKey`.
