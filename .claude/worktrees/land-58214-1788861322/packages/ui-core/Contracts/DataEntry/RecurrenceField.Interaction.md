# RecurrenceField — Interaction Contract

- **Component:** RecurrenceField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RecurrenceField.Semantic.md) · [Interaction](./RecurrenceField.Interaction.md) · [Accessibility](./RecurrenceField.Accessibility.md) · [Styling](./RecurrenceField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RecurrenceField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how each sub-control interacts, including conditional
visibility, weekday toggle state, interval clamping, and disabled state.

---

## 2. Frequency selector

- **Trigger:** change on the native `<select>`.
- **Behaviour:** `onChange({ ...value, frequency: newFreq })`.
- **Conditional side effects:** changing frequency to `'once'` collapses the
  interval, weekday, and end-date controls. Changing from `'weekly'` to another
  frequency hides the weekday group (existing `weekdays` data is preserved in
  value, just not visible/editable).

---

## 3. Interval number input

- **Visibility:** shown only when `frequency !== 'once'`.
- **Trigger:** change event.
- **Clamping:** `Math.max(1, parseInt(e.target.value) || 1)`. The interval is
  always ≥ 1. No upper clamp beyond the native `max={99}` attribute.
- **Behaviour:** `onChange({ ...value, interval: clamped })`.
- **Unit label** (non-interactive): `"day(s)"`, `"week(s)"`, `"month(s)"`,
  or `"year(s)"` based on `frequency`.

---

## 4. Weekday toggles

- **Visibility:** shown only when `frequency === 'weekly'`.
- **Trigger:** click on a weekday button.
- **Behaviour:** toggle — if `day` is in `value.weekdays`, remove it; otherwise
  add it. Result array is sorted (ascending day index 0–6).
- **`aria-pressed`:** `true` when selected, `false` otherwise.
- **No minimum/maximum constraint.** Zero weekdays can be selected (the
  recurrence would produce no occurrences — host responsibility to validate).

---

## 5. End date picker

- **Visibility:** shown only when `frequency !== 'once'`.
- **Trigger:** change event on `<input type="date">`.
- **Behaviour:**
  - Non-empty: `onChange({ ...value, endDate: e.target.value })`.
  - Empty: `onChange({ ...value, endDate: undefined })`.
- **No min-date.** The end date is not constrained to be in the future or after
  a start date.

---

## 6. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** all sub-controls receive native `disabled`. `onChange` cannot
  fire. Visual: `disabled:bg-gray-50` on inputs/selects.

---

## 7. Keyboard behaviour

All sub-controls use native browser keyboard semantics:

| Control | Key | Behaviour |
|---|---|---|
| Frequency `<select>` | Arrow keys | Select option |
| Interval `<input>` | Digit keys | Update value |
| Weekday buttons | Enter / Space | Toggle weekday (native button) |
| End date `<input type="date">` | Browser-native | Date picker |
| All | Tab / Shift+Tab | Move focus in document order |

---

## 8. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | Switching frequency preserves existing weekdays/endDate in value | Acceptable — host may want to preserve; document clearly |
| I-2 | No end-date min-date constraint | Add `min` based on an external start-date prop |
| I-3 | Zero weekdays allowed in weekly mode | Add host-level validation guidance |
