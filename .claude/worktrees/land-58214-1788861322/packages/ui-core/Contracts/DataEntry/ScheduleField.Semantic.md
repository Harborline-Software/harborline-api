# ScheduleField — Semantic Contract

- **Component:** ScheduleField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ScheduleField.Interaction.md) · [Accessibility](./ScheduleField.Accessibility.md) · [Styling](./ScheduleField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ScheduleField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled schedule entry field

---

## 1. Purpose

ScheduleField is a composite control for entering a scheduled event — a date,
start time, optional end time, and an optional all-day toggle. It renders as a
`<fieldset>` with a `<legend>`. Sub-controls are conditionally rendered based
on the `allDay` flag.

---

## 2. Data model

ScheduleField is fully controlled.

```typescript
interface ScheduleValue {
  date: string        // ISO date "YYYY-MM-DD"
  startTime: string   // "HH:MM" (24h); ignored when allDay === true
  endTime?: string    // "HH:MM" (24h); optional
  allDay?: boolean    // when true, time inputs are hidden
}

interface ScheduleFieldProps {
  value: ScheduleValue
  onChange: (value: ScheduleValue) => void
  showEndTime?: boolean   // default true
  showAllDay?: boolean    // default true
  disabled?: boolean
  minDate?: string        // "YYYY-MM-DD"; native min on date input
  label?: string          // rendered as <legend>
  id?: string             // base id prefix; default 'schedule'
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `ScheduleValue` | required | Full controlled state. |
| `onChange` | `(value: ScheduleValue) => void` | required | Fires with the immutably-updated `ScheduleValue` on any sub-control change. |
| `showEndTime` | `boolean` | `true` | When `true`, the end time input is rendered. Ignored when `allDay === true`. |
| `showAllDay` | `boolean` | `true` | When `true`, the "All day" checkbox is rendered. |
| `disabled` | `boolean` | `false` | When `true`, all sub-controls are disabled. |
| `minDate` | `string` | `undefined` | ISO date string applied as the native `min` attribute on the date input. |
| `label` | `string` | `undefined` | Rendered as `<legend>` in the `<fieldset>`. |
| `id` | `string` | `'schedule'` | Prefix for sub-control ids: `{id}-date`, `{id}-start`, `{id}-end`, `{id}-allday`. |
| `className` | `string` | `''` | Merged onto the root `<fieldset>`. |

### 3.1 Conditional rendering

| Condition | Sub-controls shown |
|---|---|
| Always | Date input |
| `!value.allDay` | Start time input |
| `!value.allDay && showEndTime` | End time input |
| `showAllDay` | All-day checkbox |

### 3.2 All-day toggle

When `allDay === true`, the start time and end time inputs are hidden. The
`date` field remains active. When `allDay` is toggled off, the time inputs
reappear using their existing `value.startTime` / `value.endTime` values.

### 3.3 End time constraint

The end time input has `min={value.startTime}`, constraining it to be at or
after the start time via browser native validation.

### 3.4 Value mutation pattern

All updates use a helper: `set<K>(key, v) = onChange({ ...value, [key]: v })`.
This is an immutable spread.

### 3.5 FormField integration

ScheduleField does NOT consume `FormFieldContext`. It renders as a semantic
`<fieldset>` + `<legend>` which is the HTML-native alternative for grouped
form controls.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `ScheduleValue` | Any sub-control changes (date, startTime, endTime, allDay). |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | No `error` per sub-field |
| S-2 | No `aria-describedby` wiring |
| S-3 | `endTime` has a native `min` constraint but no error feedback when violated |
| S-4 | `startTime` has no `min` constraint relative to the selected `date` |
