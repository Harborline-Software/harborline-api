# Filter — Semantic Contract

- **Component:** Filter
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Filter.Interaction.md) · [Accessibility](./Filter.Accessibility.md) · [Styling](./Filter.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Filter.tsx`
- **Catalog row:** #59 Filter (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled filter panel

---

## 1. Component purpose

**Filter** — a dynamic filter builder UI. Renders a list of filter rows, each consisting of a field selector, an operator selector, and a value input. Users can add and remove rows. Outputs an array of `FilterDescriptor` objects.

---

## 2. Props

```typescript
interface FilterField {
  name: string
  label: string
  type: 'text' | 'number' | 'date' | 'boolean'
  operators?: string[]      // reserved; M1 ignores; uses built-in defaults
}

interface FilterDescriptor {
  field: string
  operator: FilterOp
  value: string | number | boolean
}

interface FilterProps {
  value?: FilterDescriptor[]         // controlled
  defaultValue?: FilterDescriptor[]  // default: []
  onValueChange?: (filter: FilterDescriptor[]) => void
  fields: FilterField[]              // required; available fields
  className?: string
}
```

---

## 3. Operator defaults by field type

| Type | Available operators |
|---|---|
| `text` | contains, eq, neq, startswith, endswith |
| `number` | eq, neq, gt, gte, lt, lte |
| `date` | eq (On), gt (After), lt (Before), gte (On or after), lte (On or before) |
| `boolean` | eq (Is), neq (Is not) |

The `FilterField.operators` prop is accepted but not used in M1.

---

## 4. Value input by type

- `text` → `<input type="text">`
- `number` → `<input type="number">` (value coerced to `Number`)
- `date` → `<input type="date">`
- `boolean` → `<select>` with True / False options (value coerced to `boolean`)

---

## 5. Initial state

A new filter row (added via "Add filter") initializes with `field = fields[0].name`, `operator = defaultOperators[field.type][0].value`, `value = ''`.
