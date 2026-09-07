# SortControl — Semantic Contract

- **Component:** SortControl
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SortControl.Interaction.md) · [Accessibility](./SortControl.Accessibility.md) · [Styling](./SortControl.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SortControl.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled sort direction button

---

## 1. Purpose

SortControl is a compact two-control sort selector: a native `<select>` for the sort field, and a toggle button for ascending/descending direction. It renders in the ListToolbar right actions cluster.

Key design note (from source): SortControl uses a native `<select>` intentionally. A Radix Select would move options into a portal on open, breaking `getByRole('option')` in tests which require DOM-present options. The test compatibility trade-off is the reason for the native element choice.

---

## 2. Data model

```typescript
interface SortOption {
  value: string
  label: string
}

interface SortState {
  field: string
  direction: 'asc' | 'desc'
}

interface SortControlProps {
  options: SortOption[]
  value: SortState | null
  onChange: (next: SortState) => void
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `options` | `SortOption[]` | _required_ | The available sort fields. Each has a `value` (field key) and `label` (display name). |
| `value` | `SortState \| null` | _required_ | The current sort state. `null` means "no sort applied" — the toggle shows the neutral icon (`ArrowUpDown`). |
| `onChange` | `(next: SortState) => void` | _required_ | Called after each field change or direction toggle with the new complete sort state. |

### 3.1 Derived state

```typescript
const currentField = value?.field ?? options[0]?.value ?? ''
const currentDir = value?.direction ?? 'asc'
```

When `value` is `null`, the field defaults to `options[0].value` and direction defaults to `'asc'`. The toggle button still shows `ArrowUpDown` (neutral) when `value === null`.

### 3.2 Field change

When the user changes the `<select>`:

```typescript
onChange({ field: e.target.value, direction: currentDir })
```

The direction is preserved across field changes.

### 3.3 Direction toggle

When the user clicks the toggle button:

```typescript
onChange({ field: currentField, direction: currentDir === 'asc' ? 'desc' : 'asc' })
```

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onChange` | `(next: SortState) => void` | After field select change OR direction toggle button click |

---

## 5. Slots

SortControl has no slot props.

---

## 6. Component composition

- **ListToolbar `actions` slot.** SortControl renders in the right cluster next to ExportCsvButton and ColumnVisibilityMenu.
- **SavedViews integration.** The `SortState` is stored in `currentState.sort` in SavedViewsMenu.

---

## 7. Deferred features

- **Multi-column sort** — multiple sort fields with priority order. Deferred; `value` is a single `SortState`.
- **Sort removal** — an explicit "no sort" option. Deferred; `value` is externally nulled.
- **Custom option group** — `<optgroup>` for categorized sort fields. Deferred; current implementation has a flat list.
