# DataExportButton — Semantic Contract

- **Component:** DataExportButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DataExportButton.Interaction.md) · [Accessibility](./DataExportButton.Accessibility.md) · [Styling](./DataExportButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/DataExportButton.tsx`
- **Catalog row:** #A28 DataExportButton (`app-priority: medium`, `library-scope: v1`) — Harborline-native data export trigger button
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<button>` with programmatic download

---

## 1. Component purpose

**DataExportButton** — a button (or button-with-dropdown) that triggers data export in one or more formats. Manages its own per-format loading state. When a single format is provided, renders a direct button. When multiple formats are provided, renders a dropdown menu listing all available formats.

---

## 2. Props

```typescript
export type ExportFormat = 'csv' | 'xlsx' | 'pdf' | 'json'

interface DataExportButtonProps {
  formats?: ExportFormat[]           // default: ['csv', 'xlsx']
  onExport: (format: ExportFormat) => void | Promise<void>
  label?: string                     // default: 'Export'
  disabled?: boolean                 // default: false
  loading?: boolean                  // external loading override; default: false
  className?: string
}
```

---

## 3. Single-format mode

When `formats.length === 1`, renders a single `<button>` that directly calls `onExport(formats[0])` on click. No dropdown rendered.

---

## 4. Multi-format mode

When `formats.length > 1`, renders a disclosure button (`aria-haspopup="menu"`, `aria-expanded`) that opens a `role="menu"` dropdown listing all formats. Each format is a `role="menuitem"` button.

---

## 5. Loading state

Two loading sources: external `loading` prop and internal `busy` state (set per-format during async `onExport`). The combined `isLoading = loading || busy !== null` governs the spinner and disabled state. `busy` tracks which format is currently exporting — only one format can export at a time (sequential).

---

## 6. Format display

Format labels from `FORMAT_LABEL` record: `csv='CSV (.csv)'`, `xlsx='Excel (.xlsx)'`, `pdf='PDF (.pdf)'`, `json='JSON (.json)'`. Format icons are emoji (`FORMAT_ICON`) displayed as `aria-hidden`.
