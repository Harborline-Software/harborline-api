# FilterChips — Semantic Contract

- **Component:** FilterChips
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FilterChips.Interaction.md) · [Accessibility](./FilterChips.Accessibility.md) · [Styling](./FilterChips.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FilterChips.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled chip list for active filters

---

## 1. Purpose

FilterChips renders a set of available filter options as toggleable chip buttons. The user clicks chips to select/deselect filter values. This is a **filter selection control** — not a display of active filters (see FilterBar for that).

FilterChips supports both single-select and multi-select modes. Each chip optionally shows a count (e.g., "Paid (42)").

The component is controlled: selection state is owned by the host via `value` / `onChange`.

---

## 2. Data model

```typescript
interface FilterChipOption<T extends string = string> {
  value: T
  label: string
  count?: number
}

interface FilterChipsProps<T extends string = string> {
  options: FilterChipOption<T>[]
  value?: T | T[]
  multiple?: boolean
  onChange?: (value: T | T[]) => void
  className?: string
}
```

The component is generic on `T extends string`, allowing typed option values.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `options` | `FilterChipOption<T>[]` | _required_ | The available filter options. Each has a `value`, `label`, and optional `count`. |
| `value` | `T \| T[]` | `undefined` | Currently selected value(s). For `multiple: false`, a single `T` or an empty array (deselected). For `multiple: true`, an array of selected `T` values. |
| `multiple` | `boolean` | `false` | When `true`, multiple options can be selected simultaneously. When `false`, selecting one deselects any previous selection. |
| `onChange` | `(value: T \| T[]) => void` | `undefined` | Called with the new selection after each toggle. Signature changes with `multiple`: single mode → `T` (selected) or `T[]` (empty, deselected); multi mode → `T[]`. |
| `className` | `string` | `undefined` | Classes on the root `<div>` container. |

### 3.1 Selection derivation

Selected values are computed as a `Set<T>` from `value`:

```typescript
const selected = useMemo<Set<T>>(() => {
  if (!value) return new Set()
  return new Set(Array.isArray(value) ? value : [value])
}, [value])
```

### 3.2 Toggle semantics

**Single-select mode (`multiple: false`):**
- Selecting an unselected chip → `onChange(chipValue)`
- Selecting the currently-selected chip → `onChange([])` (deselect)

**Multi-select mode (`multiple: true`):**
- Selecting → adds to set → `onChange([...next])`
- Deselecting → removes from set → `onChange([...next])`

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onChange` | `(value: T \| T[]) => void` | After each chip toggle |

---

## 5. Slots

FilterChips has no slot props.

---

## 6. Component composition

- **ListToolbar `filters` slot.** FilterChips renders in the middle cluster of ListToolbar as quick filter toggles.
- **FilterBar pairing.** After FilterChips selection, the host maps selected values to FilterBar chips for display.
- **DataGrid filter bar.** FilterChips provides column-specific filter options (e.g., status values).

---

## 7. Deferred features

- **Disabled individual options** — a `disabled?: boolean` per option. Deferred.
- **Overflow / "more" affordance** — when many options overflow the available width, a "+ N more" expand button. Deferred.
- **Loading state** — chips showing a spinner while option counts load. Deferred.
- **Keyboard deselect-all** — pressing Escape to clear selection. Deferred to host.
