# DurationField — Interaction Contract

- **Component:** DurationField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DurationField.Semantic.md) · [Interaction](./DurationField.Interaction.md) · [Accessibility](./DurationField.Accessibility.md) · [Styling](./DurationField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DurationField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes how each segment of DurationField responds to user
input, including clamping behaviour, the colon separator, and disabled state.

---

## 2. Segment inputs

DurationField renders 2 or 3 `<input type="number">` elements depending on
`format`. Each segment is independent — editing one segment recomputes the
total seconds and fires `onChange`.

---

## 3. Hours segment (`format === 'hm' | 'hms'`)

- **Type:** `number`
- **Range:** 0 – `maxHours` (default 99).
- **Clamping:** `Math.min(maxHours, Math.max(0, parseInt(value) || 0))`.
  Non-numeric input coerces to 0.
- **`onChange` fires** with the new total seconds after clamping.

---

## 4. Minutes segment

- **Type:** `number`
- **Range for `'hm'` and `'hms'`:** 0–59.
- **Range for `'ms'`:** 0–9999 (minutes accumulate; hours fold in).
- **Clamping:** `Math.min(59, Math.max(0, parseInt(value) || 0))` for `'hm'`/`'hms'`;
  same clamp formula applies for `'ms'` (capped at 59 per implementation).
  > Note: the `'ms'` range cap is implemented as `max={format === 'ms' ? 9999 : 59}`
  > on the native input's `max` attribute, but the clamping logic still uses 59.
  > This is an inconsistency — the native max allows 9999 but `onChange` clamps
  > to 59 in `'ms'` mode. A known gap.

---

## 5. Seconds segment (`format === 'hms'`)

- **Type:** `number`
- **Range:** 0–59.
- **Clamping:** `Math.min(59, Math.max(0, parseInt(value) || 0))`.

---

## 6. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** all segment inputs receive native `disabled`. `onChange` cannot
  fire. Visual: `bg-gray-50 opacity-50`.

---

## 7. Keyboard behaviour

Each segment is a native `<input type="number">`. Standard keyboard semantics:

| Key | Behaviour |
|---|---|
| Digit keys | Update the segment value; `onChange` fires. |
| Up / Down arrows | Increment / decrement the input value by 1 (browser native `type="number"` step). `onChange` fires. |
| Tab / Shift+Tab | Move focus between segments and surrounding form elements. |
| Enter | Submits enclosing `<form>`. |

> **Note:** Spin-button browser UI for `type="number"` is visible by default
> on desktop browsers. The styling suppresses native spin buttons via
> `[appearance:textfield]` — but DurationField does NOT suppress them (only
> NumberStepper and NumericInput do). Hosts should expect native number
> increment/decrement arrows in the segments.

---

## 8. Separator display

Colon separators (`:`) between segments are rendered as non-interactive `<span>`
elements with `aria-hidden` (they are `select-none`). They have no interactive
behaviour.

---

## 9. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | Minutes clamped to 59 in `'ms'` format despite `max={9999}` on native input | Align `handleM` clamp with `'ms'` format: use `Math.min(9999, ...)` |
| I-2 | No focus-advance between segments | Would improve UX; e.g. auto-advance from hours to minutes after 2 digits |
| I-3 | No `onBlur` / `onFocus` events exposed | Add if hosts need blur-based validation |
