# MonthYearPicker — Semantic Contract

- **Component:** MonthYearPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MonthYearPicker.Interaction.md) · [Accessibility](./MonthYearPicker.Accessibility.md) · [Styling](./MonthYearPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MonthYearPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled month/year date picker variant

---

## 1. Purpose

MonthYearPicker is an inline picker for selecting a month-year combination. It
renders a year navigation row (prev/next arrows + current year display) above a
4-column grid of abbreviated month names. It stores the selection as a
`"YYYY-MM"` string. It is self-contained and renders its own label, error
message, and required indicator.

---

## 2. Data model

MonthYearPicker is semi-controlled for selection and internally stateful for
the displayed year.

```typescript
interface MonthYearPickerProps {
  value?: string          // selected month-year as "YYYY-MM"; undefined = no selection
  onChange?: (value: string) => void  // fires with "YYYY-MM" on month selection
  minYear?: number        // default: current year - 10
  maxYear?: number        // default: current year + 5
  disabled?: boolean
  label?: string
  id?: string             // used as prefix for label id
  required?: boolean
  error?: string          // error message string; renders <p role="alert"> below picker
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string` | `undefined` | Selected month-year as `"YYYY-MM"`. When absent, no month is selected. `parseValue` parses it into `{ year, month }` for comparison. |
| `onChange` | `(value: string) => void` | `undefined` | Fires with `"YYYY-MM"` when a month button is clicked. Does not fire on year navigation. |
| `minYear` | `number` | `currentYear - 10` | Minimum displayed year. The prev-year button is disabled when `displayYear <= minYear`. |
| `maxYear` | `number` | `currentYear + 5` | Maximum displayed year. The next-year button is disabled when `displayYear >= maxYear`. |
| `disabled` | `boolean` | `false` | When `true`, all buttons are disabled; `onChange` cannot fire. Visual: `opacity-50`. |
| `label` | `string` | `undefined` | Rendered as a `<label>` above the picker (not sr-only — visible). Also used to derive `aria-label` on the group. |
| `id` | `string` | `undefined` | Used as `{id}-label` for the label element's `id`. |
| `required` | `boolean` | `undefined` | When `true`, a red asterisk is appended to the label (with `aria-hidden`). Sets `aria-required` on the group container. |
| `error` | `string` | `undefined` | When provided, renders a `<p role="alert">` error message below the picker AND applies a red border to the container. |
| `className` | `string` | `undefined` | Merged onto the outer flex container. |

### 3.1 Display year vs. selected year

The displayed year is internal state (`displayYear`), initialized from
`value?.year` or the current year. The displayed year determines which year's
months are shown. The selected month-year is the `value` prop — it is
highlighted only when both year and month match.

### 3.2 Value format

`"YYYY-MM"` — e.g., `"2026-06"` for June 2026. Month is 1-indexed and
zero-padded. `parseValue` uses `split('-').map(Number)` which is sensitive to
format correctness.

### 3.3 Error rendering

When `error` is provided:
- A red border is applied to the container (`border-red-500`).
- A `<p role="alert">` is rendered below the container with `text-xs text-red-600`.

Unlike most other form components in this family, MonthYearPicker renders its
own error message rather than delegating to a parent FormField.

### 3.4 FormField integration

MonthYearPicker does NOT consume `FormFieldContext`. It manages its own label
and error rendering.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(value: string)` | User clicks an enabled month button. Payload is `"YYYY-MM"` for the selected month in the currently displayed year. |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | No FormField integration — label/error are rendered internally |
| S-2 | No `aria-describedby` wiring |
| S-3 | Display year does not sync when `value` changes after mount |
| S-4 | No individual-month disable (e.g., disable months before a `minDate`) |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (required already present; error as boolean flag + FormField integration via S-1), FR-2 (onFocus/onBlur — gap S-2 focus ring wiring), FR-3 (size/fillMode/rounded — resolves audit P1 "no size variants").
> Supersedes: S-1 (FormField integration — REQUIRED at Wave-N), S-2 (ariaDescribedBy — REQUIRED at Wave-N), S-4 (per-month disabling — REQUIRED at Wave-N for financial use cases).

### 6. Additional props

```typescript
// Added in Wave-N
interface MonthYearPickerPropsExpansion {
  // FR-2: focus events (resolves S-2 focus ring wiring gap)
  onFocus?: React.FocusEventHandler<HTMLElement>
  onBlur?: React.FocusEventHandler<HTMLElement>

  // FR-3: size axis (resolves audit P1 "no size variants")
  size?: 'sm' | 'md' | 'lg'      // default: 'md'

  // S-1: FormField integration (resolves gap)
  // MonthYearPicker now reads FormFieldContext: id, describedBy, required, disabled
  // Own label/error props remain as overrides when used standalone

  // S-2: ARIA linking (resolves gap)
  ariaDescribedBy?: string     // supplements/overrides FormFieldContext.describedBy
  ariaLabelledBy?: string      // replaces aria-label fallback when provided

  // S-4: per-month disabling (resolves audit P1 "cannot disable future months")
  disabledMonths?: string[]    // array of "YYYY-MM" strings; those months are non-interactive
  minMonth?: string            // "YYYY-MM" — months before this are disabled (inclusive)
  maxMonth?: string            // "YYYY-MM" — months after this are disabled (inclusive)

  // S-3: cursor sync on value change
  // Resolved by syncing displayYear to value.year in a useEffect — no new prop needed
}
```

### 7. FormField integration (resolves S-1)

MonthYearPicker now calls `useFormField()` to read `{ id, describedBy, required, disabled }` from `FormFieldContext`. Context values are the defaults; local props override:
- `id` from context → `{id}-label` for the internal label element
- `describedBy` from context → `aria-describedby` on the group (combined with `ariaDescribedBy` prop via `[describedBy, ariaDescribedBy].filter(Boolean).join(' ')`)
- `required` from context or local prop (logical OR) — sets `aria-required` and renders asterisk
- `disabled` from context or local prop (logical OR) — disables all buttons

The internal label/error rendering remains as fallback for standalone use.

### 8. disabledMonths / minMonth / maxMonth (resolves S-4)

A month is disabled when its `"YYYY-MM"` string appears in `disabledMonths`, OR when it is lexicographically before `minMonth`, OR after `maxMonth`. Disabled months render with `opacity-50 cursor-not-allowed` and receive `aria-disabled="true"`. `onChange` does not fire for disabled months.

### 9. Cursor sync (resolves S-3)

`displayYear` is synced to `value.year` via `useEffect([value])` when the `value` prop changes after mount. This resolves S-3 — the cursor now follows external prop changes.

### 10. size axis (FR-3)

`size` scales the month button and year navigation row. The Styling Wave-N §4 documents the recipes.
