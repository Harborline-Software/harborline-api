# ColumnVisibilityMenu — Semantic Contract

- **Component:** ColumnVisibilityMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ColumnVisibilityMenu.Interaction.md) · [Accessibility](./ColumnVisibilityMenu.Accessibility.md) · [Styling](./ColumnVisibilityMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ColumnVisibilityMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Radix UI `@radix-ui/react-popover` + `@radix-ui/react-checkbox`

---

## 1. Purpose

ColumnVisibilityMenu is a popover-style control that lets users toggle which columns are visible in a DataGrid or table. It is part of the ListToolbar's right cluster, adjacent to SortControl.

Key design decisions (from source comments):

- **State is lifted.** ColumnVisibilityMenu is pure controlled — it holds no internal column-visible state. The host page manages visibility state so it can be included in a SavedView.
- **Built on Radix Popover + Radix Checkbox.** Click-outside and Escape dismissal are handled by Radix; ColumnVisibilityMenu does not install manual DOM listeners.

---

## 2. Data model

```typescript
interface ColumnDef {
  id: string
  label: string
  /** When true, this column cannot be hidden. Defaults to false. */
  required?: boolean
}

interface ColumnVisibilityMenuProps {
  columns: ColumnDef[]
  visibility: Record<string, boolean>
  onChange: (next: Record<string, boolean>) => void
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `columns` | `ColumnDef[]` | _required_ | The full ordered list of column definitions. Each entry names a column and whether it is required (un-hideable). |
| `visibility` | `Record<string, boolean>` | _required_ | Map of `column.id → visible`. A value of `false` means hidden; `true` or absent means visible. The component treats any non-`false` value (including missing keys) as visible. |
| `onChange` | `(next: Record<string, boolean>) => void` | _required_ | Called with the full updated visibility map after each toggle. |

### 3.1 `ColumnDef.required` semantics

When `required: true`, the corresponding checkbox is rendered disabled (`disabled={true}` on Radix Checkbox). The column cannot be hidden by the user. Required columns are excluded from the `hiddenCount` badge calculation.

### 3.2 `hiddenCount` derived state

The trigger button shows a badge count when any non-required columns are hidden:

```typescript
const hiddenCount = columns.filter(
  (c) => !c.required && visibility[c.id] === false,
).length
```

This is internal to the component — callers do not pass a count.

### 3.3 Toggle semantics

On checkbox change for column `id`:

```typescript
const currentlyVisible = visibility[id] !== false
onChange({ ...visibility, [id]: !currentlyVisible })
```

The `onChange` spreads the current map and flips the single key. Callers receive the full updated record (not a diff).

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onChange` | `(next: Record<string, boolean>) => void` | After each individual column toggle |

The popover open/close state is managed internally by Radix — no `onOpen`/`onClose` callback is exposed.

---

## 5. Slots

ColumnVisibilityMenu has no slot props. The trigger button and popover content are fully rendered by the component.

---

## 6. Component composition

- **ListToolbar `actions` slot.** ColumnVisibilityMenu is placed in the right (`ml-auto`) actions cluster of ListToolbar, typically beside SavedViewsMenu and SortControl.
- **Saved views integration.** The host passes `visibility` to SavedViewsMenu's `currentState.columns` so column visibility is persisted per saved view.
- **DataGrid.** The `visibility` record maps directly to column show/hide props — typically an array of visible column IDs or a column-def-level `hidden` flag.

---

## 7. Deferred features

- **Column reordering** — drag handles to change column order within the menu. Out of scope; separate UX.
- **Column grouping** — grouping columns by category in the popover. Out of scope.
- **Bulk toggle** — "Show all / Hide all" affordance. Deferred; easily added as an `onChange({ ...allTrue })` call.
- **Persistent defaults** — initial visibility from user preferences. The host controls `visibility` initialisation; component has no opinion on default source.
