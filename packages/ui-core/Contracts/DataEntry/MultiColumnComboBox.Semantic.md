# MultiColumnComboBox — Semantic Contract

- **Component:** MultiColumnComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MultiColumnComboBox.Interaction.md) · [Accessibility](./MultiColumnComboBox.Accessibility.md) · [Styling](./MultiColumnComboBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiColumnComboBox.tsx`
- **Catalog row:** #85 MultiColumnComboBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled multi-column combobox (no Radix)

---

## 1. Component purpose

**MultiColumnComboBox** — a combobox variant where the dropdown shows a multi-column table instead of a simple list. Supports client-side text filtering. Selected value is identified by `valueField`; display text comes from `textField`.

---

## 2. Props

```typescript
interface MultiColumnComboBoxColumn {
  field: string
  title: string
  width?: string | number
}

interface MultiColumnComboBoxProps {
  value?: string | number | null    // controlled
  defaultValue?: string | number
  onValueChange?: (value: string | number | null) => void
  data: Array<Record<string, unknown>>  // required; row data
  columns: MultiColumnComboBoxColumn[]  // required; column definitions
  textField: string                 // required; property to display in the trigger
  valueField: string                // required; property used as the value key
  placeholder?: string              // default: 'Select...'
  disabled?: boolean                // default: false
  filterable?: boolean              // default: true; client-side filter by textField
  size?: 'small' | 'medium' | 'large'        // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'    // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full'  // default: 'medium'
  className?: string
}
```

---

## 3. Data model

`data` is a flat array of objects. Each row is accessed via `row[field]`. Values are compared with `==` (loose equality) for `valueField` match.

---

## 4. Filtering

When `filterable=true`, the client-side filter checks if `String(row[textField]).toLowerCase().includes(filter.toLowerCase())`. Filter activates on focus and on typing in the input.

---

## 5. Display text

Trigger shows `row[textField]` of the selected row. When no selection, shows `placeholder`.

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** MultiColumnComboBox ~50% Kendo-minimum coverage. P1 misses: server-side
filtering / `onFilterChange`, `loading`, `valid` / `required`.

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `MultiColumnComboBoxProps`:

```typescript
required?: boolean   // default: false — aria-required="true" on input
error?: boolean      // default: false — aria-invalid="true" on input + error ring on wrapper
```

`validationMessage` is NOT added — message composition via `FormField` / `ValidationMessage`
per FR-1 §3.

**wave-E1**

---

### §E1-2 FR-2 adoption — focus + popup events (family ruling FR-2)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `MultiColumnComboBoxProps`:

```typescript
onFocus?: React.FocusEventHandler<HTMLInputElement>
onBlur?: React.FocusEventHandler<HTMLInputElement>
open?: boolean
onOpenChange?: (open: boolean) => void
```

`open` / `onOpenChange` adopt the FR-2 canonical pair. The existing internal open/close state
machine (already defined in the Interaction contract) becomes the uncontrolled default.

**wave-E1**

---

### §E1-3 FR-3 size-vocabulary migration (family ruling FR-3)

`size` values migrate from `'small' | 'medium' | 'large'` → `'sm' | 'md' | 'lg'`.
`rounded` migrates from `'small' | 'medium' | 'large' | 'full'` → `'sm' | 'md' | 'lg' | 'full'`.
Deprecation aliases accepted at runtime; removed at next major.

**wave-E1**

---

### §E1-4 Loading state with popup suppression (audit P1)

Add to `MultiColumnComboBoxProps`:

```typescript
loading?: boolean   // default: false
```

**Semantics:**

1. Spinner in trailing slot (animated); replaces toggle chevron while `loading={true}`.
2. `aria-busy="true"` on wrapper.
3. **Popup suppression:** popup MUST NOT open while loading (toggle button `disabled`; ArrowDown
   is no-op).
4. Table header row remains rendered in the DOM; it is hidden until popup opens normally.

**wave-E1**

---

### §E1-5 Server-side filtering / `onFilterChange` (audit P1)

Add to `MultiColumnComboBoxProps`:

```typescript
onFilterChange?: (value: string) => void
```

When `onFilterChange` is provided:
- `filterable` prop should be `false` (host controls `data` externally).
- Fires on every keystroke in the filter input (no debounce — host-owned).
- Host updates `data` with server-filtered rows; `loading` should be set during the fetch.

**Pattern:**

```tsx
const [query, setQuery] = useState('')
const { data: rows, isFetching } = useQuery(['items', query], () => fetchItems(query))

<MultiColumnComboBox
  data={rows ?? []}
  columns={columns}
  textField="name"
  valueField="id"
  filterable={false}
  onFilterChange={setQuery}
  loading={isFetching}
  ...
/>
```

**wave-E1**

---

### §E1-6 ARIA combobox — mandatory rows (WCAG Level A; closes G-MCCB3/G-MCCB4/G-MCCB5)

**Required attributes (implementation obligations; no new JS-facing props):**

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `aria-controls` | `<input role="combobox">` | `"{id}-popup"` | G-MCCB5 |
| `aria-activedescendant` | `<input role="combobox">` | `"{id}-row-{index}"` of highlighted row | G-MCCB3 |
| `role="rowgroup"` | `<tbody>` inside popup table | literal | G-MCCB4 |
| `role="row"` | Each `<tr>` in popup | literal | G-MCCB4 |
| `role="gridcell"` | Each `<td>` in popup | literal | G-MCCB4 |
| `id="{id}-row-{index}"` | Each `<tr>` | literal | matches aria-activedescendant |
| `aria-selected="true/false"` | Each `<tr>` | boolean | WAI-ARIA combobox 1.2 |

The popup uses `role="grid"` (tabular combobox variant) rather than `role="listbox"`.
`id` is required; auto-generate via `useId()` if not provided.

**Keyboard:**

| Key | Behavior |
|---|---|
| `ArrowDown` | Open popup if closed; move highlight to next row |
| `ArrowUp` | Move highlight to previous row |
| `Enter` | Select highlighted row; close popup |
| `Escape` | Close popup; reset input to current selected `textField` value |
| `Tab` | Close popup; commit current selection |

**wave-E1** — WCAG Level A, mandatory before v1-ship.

---

### §E1-7 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| Virtual scrolling | wave-E2+ |
| `itemRender` (custom row) | wave-E2+ |
| `header` / `footer` popup slots | wave-E2+ |
| `popupSettings` (width, animate) | wave-E2+ |
| Adaptive mode | wave-E2+ |
| `clearButton` | wave-E2+ |
