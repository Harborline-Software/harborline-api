# Filter — Interaction Contract

- **Component:** Filter
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Filter.Semantic.md) · [Accessibility](./Filter.Accessibility.md) · [Styling](./Filter.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Filter.tsx`
- **Catalog row:** #59 Filter (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Row operations

| Action | Effect |
|---|---|
| Click "Add filter" | Adds new row with defaults; fires `onValueChange` |
| Click "×" (remove) on row i | Removes row i; fires `onValueChange` |
| Change field selector | Updates `field`; resets `operator` to first available for new type; resets `value` to `''`; fires `onValueChange` |
| Change operator selector | Updates `operator`; fires `onValueChange` |
| Change value input | Updates `value`; fires `onValueChange` |

---

## 2. onValueChange semantics

`onValueChange` fires on every mutation: add, remove, field change, operator change, value change. It is called with the complete new `FilterDescriptor[]` array, not a diff.

---

## 3. Controlled vs uncontrolled

When `value` prop is provided (controlled), the parent owns the state and must handle `onValueChange` to update it. Internally, the component derives `filters = controlledValue ?? internalValue`.

When uncontrolled, `internalValue` is the source of truth.

---

## 4. No submit action

`Filter` has no submit or apply button. Every change fires `onValueChange` immediately (live filtering pattern).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FT1 | Low | `FilterField.operators` prop accepted but ignored — field-level operator customization not implemented | Accepted-risk M1; noted in Semantic §3 |

---

## Full-surface expansion (2026-06-11 — waves 2-4, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

**Cross-references:**
- `DataGrid.Interaction.md §FS-1.2` — operator model; Filter shares `FilterDescriptor` operator vocabulary defined there.
- `DataQuery.ts` — current `FilterDescriptor` type and `filterBy` engine (flat AND-only).
- FR-1 / FR-2 / FR-3 from `_shared/design/polish/family-rulings-2026-06-11.md`.

---

### §FS-1 Wave-2 — CompositeFilterDescriptor + AND/OR logic

**Status:** Draft

#### §FS-1.1 Type model upgrade

The current `FilterDescriptor[]` flat array (always implicit AND) is replaced by a
`CompositeFilterDescriptor` root:

```typescript
// Extends DataQuery.ts — new exports alongside existing FilterDescriptor

export type FilterLogic = 'and' | 'or'

export interface CompositeFilterDescriptor {
  logic: FilterLogic
  filters: Array<FilterDescriptor | CompositeFilterDescriptor>
}

// FilterDescriptor — operator union extended to align with DataGrid §FS-1.2:
export interface FilterDescriptor {
  field: string
  operator:
    | 'eq' | 'neq'
    | 'gt' | 'gte' | 'lt' | 'lte'
    | 'contains' | 'startswith' | 'endswith'
    | 'isnull' | 'isnotnull'           // new — no value input when selected
    | 'isempty' | 'isnotempty'         // new — string-family
  value: unknown
}
```

`isnull` / `isnotnull` / `isempty` / `isnotempty` operators require no value input.
When selected, the value input is hidden and the descriptor stores `value: ''`.
This aligns with DataGrid §FS-1.2 §3 (isnull hides value input).

The existing `filterBy` utility in `DataQuery.ts` gains an overload accepting
`CompositeFilterDescriptor`; the existing `FilterDescriptor[]` overload is preserved
for backward compatibility.

**wave-2**

---

#### §FS-1.2 Root AND/OR toggle

A root-level `logic` toggle renders above the filter rows.
Toggle appearance: a segmented control or pair of radio buttons labeled **AND** / **OR**.
Default value: `'and'`.

Behavior:
- Clicking AND or OR updates `root.logic` and fires `onValueChange` with the updated
  `CompositeFilterDescriptor`.
- The toggle is only visible when the Filter is in composite mode
  (i.e., the new `mode: 'composite'` prop is present — see §FS-1.3).
- When mode is `'flat'` (legacy), the toggle is absent; the value type remains
  `FilterDescriptor[]` for backward compatibility.

**wave-2**

---

#### §FS-1.3 Mode prop and backward compatibility

```typescript
interface FilterProps {
  // Existing props unchanged:
  fields: FilterField[]
  className?: string

  // Controlled/uncontrolled — polymorphic per mode:
  mode?: 'flat' | 'composite'   // default: 'flat' (backward-compatible)

  // flat mode (existing; value type unchanged):
  value?: FilterDescriptor[]
  defaultValue?: FilterDescriptor[]
  onValueChange?: (filter: FilterDescriptor[]) => void

  // composite mode (new):
  compositeValue?: CompositeFilterDescriptor
  defaultCompositeValue?: CompositeFilterDescriptor
  onCompositeChange?: (filter: CompositeFilterDescriptor) => void
}
```

When `mode === 'flat'`: existing behavior, existing type. No regression.
When `mode === 'composite'`: composite value model, AND/OR toggle rendered.

**wave-2**

---

#### §FS-1.4 onValueChange semantics in composite mode

`onCompositeChange` fires on every mutation: add row, remove row, field change,
operator change, value change, logic toggle, group add, group remove.
It always receives the full new `CompositeFilterDescriptor` (not a diff).
The root logic toggle fires `onCompositeChange` with updated `root.logic`;
individual row changes fire with the updated `root.filters` array.

**wave-2**

---

### §FS-2 Wave-3 — nested filter groups

**Status:** Draft

#### §FS-2.1 Group concept

A nested group is a `CompositeFilterDescriptor` nested inside `root.filters`.
Each group has its own `logic: 'and' | 'or'` toggle.

Groups are rendered as visually indented sections with a group-level AND/OR toggle
in the group header and a "Remove group" button.

Groups can nest one level deep in wave-3. Deeper nesting (group-within-group) is
deferred to a later wave; the type system supports arbitrary depth but the UI caps
at one nesting level in wave-3.

**wave-3**

---

#### §FS-2.2 Add group action

An "Add group" button appears below the "Add filter" button when `mode === 'composite'`.
Clicking it appends a new `CompositeFilterDescriptor` with `{ logic: 'and', filters: [] }`
to `root.filters`. The group renders with one empty row by default (same defaults as
"Add filter": field = `fields[0].name`, operator = first for type, value = `''`).

**wave-3**

---

#### §FS-2.3 Group AND/OR toggle

Each group header renders its own AND/OR toggle.
Clicking it updates `group.logic` and fires `onCompositeChange`.
The group toggle is visually subordinate to the root toggle (indented, smaller weight).

**wave-3**

---

#### §FS-2.4 Remove group

A "Remove group" button in the group header removes the entire
`CompositeFilterDescriptor` entry from `root.filters` and fires `onCompositeChange`.
Individual rows within the group use the standard "×" (remove row) button;
removing the last row in a group does NOT auto-remove the group — the group persists
empty until the user explicitly removes it.

**wave-3**

---

### §FS-3 Wave-4 — custom field editor slot

**Status:** Draft

#### §FS-3.1 editorRender slot

```typescript
interface FilterField {
  name: string
  label: string
  type: 'text' | 'number' | 'date' | 'boolean' | 'custom'
  operators?: string[]
  // New in wave-4:
  editorRender?: (props: FilterEditorProps) => React.ReactNode
}

interface FilterEditorProps {
  value: unknown
  onChange: (value: unknown) => void
  operator: string
  field: FilterField
}
```

When `FilterField.editorRender` is supplied, the default value-input (text/number/date/
boolean) is replaced with the result of `editorRender(props)`.
The slot is responsible for:
- Rendering the input control.
- Calling `onChange(newValue)` when the user selects/types a value.
- Handling its own `disabled` state if needed.

Filter continues to manage field selector, operator selector, and row add/remove.
The slot only replaces the value input column.

**Alignment with Kendo's per-field custom editor pattern (Kendo DataTools Filter API).**

**wave-4**

---

#### §FS-3.2 Custom operator list (closes G-FT1)

`FilterField.operators` (previously accepted-but-ignored) is wired in wave-4.
When `operators` is provided, it replaces the type-derived default operator list for
that field. Values must be strings; they are rendered as-is in the operator selector.
The consumer is responsible for ensuring the custom operators are handled by any
`filterBy` implementation they pass the output to.

**wave-4**

---

### §FS-4 Keyboard and accessibility (all waves)

**Status:** Draft (P1 — keyboard must land before any composite wave ships)

| Key | Context | Behaviour |
|---|---|---|
| Tab / Shift+Tab | Filter panel | Moves focus through: root logic toggle → filter rows (field, operator, value, remove) → Add filter button → Add group button (composite mode) |
| Enter | "Add filter" button focused | Same as click |
| Enter | "Add group" button focused | Same as click |
| Space | AND/OR toggle option | Selects that logic option |
| Delete / Backspace | Remove (×) button focused | Removes the row (same as click) |
| Escape | Value input focused | Clears the value input and fires `onValueChange` / `onCompositeChange` with `value: ''` for that row |

Per FR-2: the AND/OR toggle adopts `onFocus` / `onBlur` passthrough if implemented
as a ToggleGroup.

**wave-2** (baseline keyboard for flat + composite modes)
