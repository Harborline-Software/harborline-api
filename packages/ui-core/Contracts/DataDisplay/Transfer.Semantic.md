# Transfer — Semantic Contract

- **Component:** Transfer / ShuttleList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Transfer.Interaction.md) · [Accessibility](./Transfer.Accessibility.md) · [Styling](./Transfer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Ant Design Transfer)
- **Catalog row:** #A8 Transfer / ShuttleList (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Transfer baseline)

---

## 1. Component purpose

**Transfer** (alias: ShuttleList) — a dual-list component where items can be moved between a source list and a target list. Items are selected in one list and transferred to the other via action buttons. Used for permission assignment, multi-select with explicit confirmation, and ordered selection.

---

## 2. Props (planned)

```typescript
interface TransferItem {
  key: string
  title: string
  description?: string
  disabled?: boolean
}

interface TransferProps {
  dataSource: TransferItem[]
  targetKeys: string[]                    // controlled: keys in right list
  onChange?: (targetKeys: string[], direction: 'right' | 'left', movedKeys: string[]) => void
  selectedKeys?: string[]                 // controlled selection
  onSelectChange?: (sourceSelected: string[], targetSelected: string[]) => void
  titles?: [string, string]              // labels for left/right panels; default: ['', '']
  operations?: [string, string]          // button labels; default: ['>', '<']
  showSearch?: boolean                   // default: false
  filterOption?: (inputValue: string, item: TransferItem) => boolean
  listStyle?: React.CSSProperties | ((info: { direction: 'left' | 'right' }) => React.CSSProperties)
  disabled?: boolean
  className?: string
  children?: (info: TransferListBodyProps) => React.ReactNode  // custom list renderer
}
```

---

## 3. List panels

Left panel: source items (not yet in `targetKeys`). Right panel: target items (in `targetKeys`).

---

## 4. Search

When `showSearch=true`, each panel shows a search input. `filterOption` controls whether an item matches the search term.

---

## 5. Footer

Optional footer in each panel via the `footer` render slot (not in base spec, available as extension).
