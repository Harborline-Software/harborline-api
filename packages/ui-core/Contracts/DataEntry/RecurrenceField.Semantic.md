# RecurrenceField — Semantic Contract

- **Component:** RecurrenceField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./RecurrenceField.Interaction.md) · [Accessibility](./RecurrenceField.Accessibility.md) · [Styling](./RecurrenceField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RecurrenceField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled recurrence rule editor

---

## 1. Purpose

RecurrenceField is a composite control for configuring a recurring schedule.
It renders a frequency selector and — when the frequency is not `'once'` —
conditional sub-controls: an interval number input, a weekday toggle group
(for `'weekly'`), and an end-date picker. All sub-controls update a single
`RecurrenceValue` object via `onChange`.

---

## 2. Data model

RecurrenceField is fully controlled.

```typescript
type RecurrenceFrequency = 'once' | 'daily' | 'weekly' | 'monthly' | 'yearly'

interface RecurrenceValue {
  frequency: RecurrenceFrequency
  interval: number        // repeat every N units; minimum 1
  weekdays?: number[]     // 0–6 (Sun=0); relevant for 'weekly' frequency
  monthDay?: number       // (not yet used in the UI; reserved)
  endDate?: string        // ISO date "YYYY-MM-DD"; optional end bound
}

interface RecurrenceFieldProps {
  value: RecurrenceValue
  onChange: (value: RecurrenceValue) => void
  disabled?: boolean
  label?: string
  id?: string           // base id prefix; default 'recurrence'
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `RecurrenceValue` | required | Full controlled state. |
| `onChange` | `(value: RecurrenceValue) => void` | required | Fires with the immutably-updated `RecurrenceValue` on any sub-control change. |
| `disabled` | `boolean` | `false` | When `true`, all sub-controls are disabled. |
| `label` | `string` | `undefined` | Visible label linked to the frequency selector. |
| `id` | `string` | `'recurrence'` | Prefix for sub-control ids (`{id}-freq`, `{id}-end`). |
| `className` | `string` | `''` | Merged onto the root `<div>`. |

### 3.1 Conditional rendering

| Condition | Sub-control shown |
|---|---|
| Always | Frequency `<select>` |
| `frequency !== 'once'` | Interval number input + unit label |
| `frequency === 'weekly'` | Weekday toggle group |
| `frequency !== 'once'` | End date `<input type="date">` |

### 3.2 Frequency selector

A native `<select>` with five options: `'Does not repeat'`, `'Daily'`,
`'Weekly'`, `'Monthly'`, `'Yearly'`. The selected value corresponds to
`value.frequency`.

### 3.3 Interval input

A `<input type="number">` ranging 1–99. Displays "Every N [day/week/month/year](s)"
with a unit label derived from `frequency`.

### 3.4 Weekday toggles

For `'weekly'` frequency only: seven toggle buttons (Sun–Sat) using
`aria-pressed`. Multiple weekdays can be selected simultaneously. Toggling a
selected day removes it; toggling an unselected day adds it (sorted).

### 3.5 End date

A native `<input type="date">` for an optional recurrence end date. Clearing
the field stores `endDate: undefined` in the value.

### 3.6 `monthDay` field

`monthDay` is declared in `RecurrenceValue` but not exposed in the UI.
It is reserved for future monthly-by-day-of-month support.

### 3.7 FormField integration

RecurrenceField does NOT consume `FormFieldContext`. It manages its own label
and sub-control id namespace.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `RecurrenceValue` | Any sub-control changes. Value is always a complete object (immutable spread update). |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | `monthDay` is in the interface but has no UI |
| S-2 | No `error` prop — no validation feedback |
| S-3 | No FormField integration |
| S-4 | Interval input has no `aria-label` — only a visual inline label |
| S-5 | End date `<input>` has no min-date constraint (could be before start) |
