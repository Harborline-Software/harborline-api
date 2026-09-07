# FilterBar — Semantic Contract

- **Component:** FilterBar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FilterBar.Interaction.md) · [Accessibility](./FilterBar.Accessibility.md) · [Styling](./FilterBar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/FilterBar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled filter bar wrapper

---

## 1. Purpose

FilterBar renders the set of currently-active filter chips in a list or DataGrid view. Each chip shows a filter label (and optionally its value), with an optional remove button. When no filters are active, it renders an empty-state placeholder.

FilterBar is a **display component** for active filters, not a filter-input control. The user removes filters via the chips; the host manages which filters are active.

Distinction from FilterChips (forms/): FilterBar renders ACTIVE filters as removable chips; FilterChips renders AVAILABLE filter options as a toggle group for selection.

---

## 2. Data model

```typescript
interface FilterChip {
  id: string
  label: string
  value?: string
  removable?: boolean
}

interface FilterBarProps {
  chips: FilterChip[]
  onRemove?: (id: string) => void
  onClearAll?: () => void
  placeholder?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `chips` | `FilterChip[]` | _required_ | The active filters to display. Each entry has an `id`, display `label`, optional `value` string, and optional `removable` flag. |
| `onRemove` | `(id: string) => void` | `undefined` | Called when the user clicks the remove button on a chip. If omitted, no remove buttons are rendered. |
| `onClearAll` | `() => void` | `undefined` | Called when the user clicks "Clear all". Only rendered when `onClearAll` is provided AND `chips.length > 1`. |
| `placeholder` | `string` | `'No active filters'` | Text shown (with a ⊘ icon) when `chips` is empty. Pass `undefined` or `''` to render nothing when empty. |
| `className` | `string` | `''` | Additional classes on the root element. |

### 3.1 `FilterChip.removable` semantics

- `removable !== false` (default / undefined / true) AND `onRemove` provided: remove button shown.
- `removable === false` OR `onRemove` not provided: no remove button.

A chip with `removable: false` is a read-only indicator (e.g., a tenant-scoped system filter that cannot be removed by the user).

### 3.2 `FilterChip.value` semantics

When `value` is provided, the chip renders as `Label: value` (e.g., "Status: Paid"). When absent, only `label` is shown.

### 3.3 Empty state

When `chips.length === 0` AND `placeholder` is truthy: renders the placeholder text with a `⊘` decorative icon. The placeholder container uses `text-gray-400`.

When `chips.length === 0` AND `placeholder` is falsy: renders nothing (returns `null`).

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onRemove` | `(id: string) => void` | User clicks the remove button on a chip |
| `onClearAll` | `() => void` | User clicks the "Clear all" text button (only present when `chips.length > 1`) |

---

## 5. Slots

FilterBar has no slot props. All content is derived from the `chips` array.

---

## 6. Component composition

- **ListToolbar below the toolbar row.** FilterBar typically renders below the ListToolbar as a "active filters" indicator row.
- **FilterChips paired usage.** FilterChips (forms/) provides the filter selection UI; FilterBar displays the result. The host translates selected FilterChips values into FilterBar chips.
- **SearchInput pairing.** A search term may also appear as a FilterBar chip (e.g., `{id: "search", label: "Search", value: "tenant"}`).

---

## 7. Deferred features

- **Chip edit-in-place** — clicking a chip to re-open the filter picker. Deferred; host composes.
- **Chip colour coding** — different chip colours per filter category. Currently all blue; colour variants deferred.
- **Filter count summary** — e.g., "(3 filters)" summary text. Deferred; host computes.
