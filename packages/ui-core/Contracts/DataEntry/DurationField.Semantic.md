# DurationField — Semantic Contract

- **Component:** DurationField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DurationField.Interaction.md) · [Accessibility](./DurationField.Accessibility.md) · [Styling](./DurationField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DurationField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled duration entry

---

## 1. Purpose

DurationField is a compound input for entering a time duration as hours,
minutes, and/or seconds. The internal value is a single integer representing
**total seconds**. Three display formats are supported: `'hm'` (hours +
minutes), `'hms'` (hours + minutes + seconds), and `'ms'` (minutes + seconds,
treating total minutes without hour decomposition).

---

## 2. Data model

DurationField is fully controlled. The host owns the total-seconds value.

```typescript
type DurationFormat = 'hm' | 'hms' | 'ms'

interface DurationFieldProps {
  value: number            // total seconds; non-negative
  onChange: (seconds: number) => void
  format?: DurationFormat  // default 'hm'
  maxHours?: number        // upper bound on the hours segment; default 99
  disabled?: boolean
  label?: string           // sr-only label text
  id?: string              // overrides derived id
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `number` | required | Total seconds. Negative values are clamped to 0 at render. |
| `onChange` | `(seconds: number) => void` | required | Fires when any segment changes. Payload is the new total seconds (always non-negative). |
| `format` | `DurationFormat` | `'hm'` | Controls which segments are rendered. `'hm'` → hours + minutes; `'hms'` → hours + minutes + seconds; `'ms'` → minutes + seconds (minutes are unbounded — total hours fold into minutes). |
| `maxHours` | `number` | `99` | Maximum value allowed for the hours segment. Has no effect in `'ms'` format. |
| `disabled` | `boolean` | `false` | Disables all segment inputs. |
| `label` | `string` | `undefined` | When provided, a `<label>` with `className="sr-only"` is rendered linked to the first segment's `id`. Visual-only label is not rendered. |
| `id` | `string` | `useId()` | Overrides the auto-generated id. The hours segment (or minutes segment in `'ms'` format) uses this id; other segments have no explicit id in M1. |
| `className` | `string` | `''` | Merged onto the root flex container. |

### 3.1 Format semantics

| Format | Segments shown | Minutes range | Notes |
|---|---|---|---|
| `'hm'` | hours, minutes | 0–59 | Seconds are fixed at 0 in the result |
| `'hms'` | hours, minutes, seconds | 0–59 | Full HMS decomposition |
| `'ms'` | minutes, seconds | 0–9999 | No hour segment; minutes accumulate hours (e.g. 90 min instead of 1h 30m) |

### 3.2 `id` assignment

The `id` is resolved as: `idProp ?? ctx?.inputId ?? genId` (from
`useFormFieldContext` or `useId()`). In `'hm'` and `'hms'` formats, the hours
input receives the resolved `id`. In `'ms'` format (no hours input), the
minutes input receives it.

### 3.3 FormField integration

DurationField reads `ctx?.inputId` from `useFormFieldContext()` (a context
variant) but does NOT read `describedBy`. `aria-describedby` is not wired.
This is a gap (see §7).

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(seconds: number)` | Any segment (hours, minutes, or seconds) is changed. Segments are clamped before the total is computed. |

---

## 5. Segment display

Each segment displays as a zero-padded 2-digit string (via the `pad` helper):
`pad(h)` → `"01"`, `"09"`, etc. The underlying input `type="number"` but
displays the padded value.

---

## 6. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | `aria-describedby` not wired from FormFieldContext |
| S-2 | No `error` prop — no validation feedback surface |
| S-3 | Only the first segment has an accessible `id`; subsequent segments carry no `id` |
| S-4 | No `onBlur` / `onFocus` surface |
| S-5 | Seconds clamped to 0–59 even in `'ms'` format per `handleS`; `'ms'` seconds behave identically to `'hms'` |
