# ScheduleField — Interaction Contract

- **Component:** ScheduleField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScheduleField.Semantic.md) · [Interaction](./ScheduleField.Interaction.md) · [Accessibility](./ScheduleField.Accessibility.md) · [Styling](./ScheduleField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ScheduleField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how ScheduleField's date, time, and all-day sub-
controls interact, including conditional rendering, end-time constraints, and
disabled state.

---

## 2. Date input

- **Type:** `<input type="date">`.
- **Trigger:** change event.
- **Behaviour:** `onChange({ ...value, date: e.target.value })`.
- **Min date:** `min={minDate}` from prop constrains selectable dates via
  browser native validation.
- **Always visible** regardless of `allDay` state.

---

## 3. Start time input

- **Type:** `<input type="time">`.
- **Visibility:** shown when `!value.allDay`.
- **Trigger:** change event.
- **Behaviour:** `onChange({ ...value, startTime: e.target.value })`.
- **No min time constraint.** Any time is valid.

---

## 4. End time input

- **Type:** `<input type="time">`.
- **Visibility:** shown when `!value.allDay && showEndTime`.
- **Trigger:** change event.
- **Behaviour:** `onChange({ ...value, endTime: e.target.value || undefined })`.
  When cleared, `endTime` is set to `undefined`.
- **`min={value.startTime}`:** browser native validation ensures end time is
  not before start time. No JS enforcement — browser shows native validation
  bubble on form submit.

---

## 5. All-day checkbox

- **Visibility:** shown when `showAllDay === true`.
- **Trigger:** change event.
- **Behaviour:** `onChange({ ...value, allDay: e.target.checked })`.
- **Side effect:** when `allDay` flips to `true`, the start/end time inputs
  are removed from the DOM. Time values are preserved in the `value` object
  but not editable. When `allDay` flips back to `false`, time inputs reappear
  with their existing values.

---

## 6. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** all sub-controls receive native `disabled`. `onChange` cannot
  fire. Visual: `disabled:bg-gray-50 disabled:text-gray-400` on inputs.

---

## 7. Keyboard behaviour

All sub-controls use native browser keyboard semantics:

| Control | Key | Behaviour |
|---|---|---|
| Date input | Browser native date picker | Arrow keys for day increment |
| Time inputs | Browser native time picker | Arrow keys for hour/minute |
| All-day checkbox | Space | Toggle |
| All controls | Tab / Shift+Tab | Move focus |

---

## 8. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | End-time `min={startTime}` enforced only by browser native validation | Add JS clamping or error feedback |
| I-2 | No validation that date is in the future (only `minDate` prop can constrain) | Document host responsibility |
| I-3 | Toggling `allDay` on/off preserves time values silently | Could optionally clear times when allDay is toggled on |
