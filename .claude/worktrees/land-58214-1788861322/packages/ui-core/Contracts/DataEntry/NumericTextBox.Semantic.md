# NumericTextBox — Semantic Contract

- **Component:** NumericTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NumericTextBox.Interaction.md) · [Accessibility](./NumericTextBox.Accessibility.md) · [Styling](./NumericTextBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericTextBox.tsx`
- **Catalog row:** #90 NumericTextBox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input>` (text display mode) + native `<input type="number">` (edit mode)

---

## 1. Component purpose

**NumericTextBox** is the canonical numeric-entry input for
`@harborline-software/ui-react`. It pairs a styled text input with optional
increment / decrement spinner buttons and a two-mode display:

- **Display mode** — when the input does not have focus, the value is
  shown formatted (e.g. `"$1,234.56"` for currency, `"75%"` for
  percentage, `"42.00"` for plain decimal).
- **Edit mode** — when the input has focus, the raw numeric value is
  shown as a plain string suitable for direct keyboard editing.

The component supports `min` / `max` clamping, configurable decimal
precision, and a small format-string vocabulary (`'c2'` = currency with
2 decimals, `'p0'` = percentage with 0 decimals).

Unlike `TextBox` / `Input`, NumericTextBox is **not** a thin styled
wrapper around `<input>` — it owns an internal state machine
(`editing` flag, `raw` edit buffer, `internal` value in uncontrolled
mode) and a non-trivial commit pipeline. The contract below documents
that state machine.

---

## 2. Data model

NumericTextBox supports both controlled (`value` + `onChange`) and
uncontrolled (`defaultValue`) modes.

```typescript
interface NumericTextBoxProps {
  // Value
  value?: number | null
  defaultValue?: number
  onChange?: (value: number | null) => void

  // Constraints
  min?: number
  max?: number
  step?: number         // default: 1 — spinner increment
  decimals?: number     // default: 2 — decimal places for display + toFixed commit
  format?: string       // 'cN' = currency (USD), 'pN' = percentage; otherwise toFixed(decimals)

  // Affordances
  spinners?: boolean    // default: true — show increment/decrement buttons
  placeholder?: string
  disabled?: boolean    // default: false
  readonly?: boolean    // default: false

  // Styling (shared with Input)
  size?: 'small' | 'medium' | 'large'             // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'         // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full' // default: 'medium'

  // Native form attributes
  id?: string
  name?: string
  className?: string
}
```

**Three pieces of internal state:**

| State | Type | Purpose |
| --- | --- | --- |
| `internal` | `number \| null` | Uncontrolled value; ignored when the host passes `value`. Initialised from `defaultValue` or `null`. |
| `editing` | `boolean` | True while the input has focus; switches display ↔ edit mode. |
| `raw` | `string` | The unparsed string the user is typing during edit mode. Independent from `value` until commit. |

**`current` (derived):** `value !== undefined ? value : internal` — the
effective value rendered in display mode and used as the spinner anchor.

The data model intentionally distinguishes `null` (no value) from `0`
(a real zero) — see §3.1.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `value` | `number \| null` | — | Controlled value. `null` represents "no value entered"; `0` represents the numeric zero. When `undefined`, NumericTextBox is uncontrolled. |
| `defaultValue` | `number` | — | Initial value for uncontrolled mode. No `null` here — uncontrolled inputs that start empty pass nothing. |
| `onChange` | `(v: number \| null) => void` | — | Commit callback (NOT per-keystroke). Fires on blur, Enter, and on every spinner click. Payload is the clamped numeric value, or `null` when the input is empty / unparseable. See §4. |
| `min` | `number` | — | Lower clamp bound. Applied at commit (blur / Enter) AND inside `spin()` for the decrement spinner. The text input does NOT prevent typing below `min` during edit mode — only the commit clamps. |
| `max` | `number` | — | Upper clamp bound. Symmetric to `min`. |
| `step` | `number` | `1` | Spinner increment delta. `spin(+1)` adds `step`; `spin(-1)` subtracts `step`. Not used by keyboard typing. |
| `decimals` | `number` | `2` | Number of decimal places for `toFixed(decimals)` display when no `format` is set. Also used as the default `N` for `'c'` format strings. |
| `format` | `string` | — | Format-code vocabulary (see §5). `'c2'` = USD currency with 2 decimals; `'p0'` = percentage with 0 decimals. When unset, display uses `current.toFixed(decimals)`. |
| `spinners` | `boolean` | `true` | When `true` AND `!readonly`, increment / decrement buttons render on the right of the input. |
| `placeholder` | `string` | — | Native input placeholder; shown when `current == null` in display mode (`fmt(null) === ''`) or `raw === ''` in edit mode. |
| `disabled` | `boolean` | `false` | Native input disabled; spinners also disabled; container styled with `opacity-50 cursor-not-allowed`. |
| `readonly` | `boolean` | `false` | Native input `readOnly`; spinners hidden entirely (not just disabled). |
| `size` | `'small' \| 'medium' \| 'large'` | `'medium'` | Same vocabulary as Input. Drives container height. |
| `fillMode` | `'solid' \| 'outline' \| 'flat'` | `'solid'` | Same vocabulary as Input. |
| `rounded` | `'small' \| 'medium' \| 'large' \| 'full'` | `'medium'` | Same vocabulary as Input. Note: `flat` does NOT override to `rounded-none` here (divergence from Input — see §11 G-NTB5). |
| `id` / `name` / `className` | — | — | Standard. `name` participates in form submission with the raw `toFixed(decimals)` string as the submitted value during display mode. |

### 3.1 `null` vs `0`

A core data-model invariant:

- `null` means **the user has not entered a value**. Display shows
  `placeholder`; the input is "empty".
- `0` means **the user entered zero**. Display shows `"0.00"` (or
  format-appropriate `"$0.00"`, `"0%"`).

Behaviour:

- `parseFloat('')` is `NaN` → `commit()` produces `null`.
- `parseFloat('0')` is `0` → `commit()` produces `0`.
- `fmt(null, ...)` returns `''` → empty display, placeholder visible.
- `fmt(0, decimals)` returns `'0.00'` (decimals=2).

Hosts MUST type `value` as `number | null`, not `number | undefined`,
to express the empty state in controlled mode. Passing `undefined`
switches NumericTextBox to uncontrolled mode (the `current` derivation
falls back to `internal`).

### 3.2 Clamping semantics

The `clamp(v)` helper applies `min` / `max` if defined:

```typescript
const clamp = (v: number) => {
  let r = v
  if (min !== undefined) r = Math.max(min, r)
  if (max !== undefined) r = Math.min(max, r)
  return r
}
```

Clamping is applied at exactly two points:

1. **`commit(str)`** — after `parseFloat`, the result is clamped before
   firing `onChange`.
2. **`spin(dir)`** — after computing `(current ?? 0) + dir * step`, the
   result is clamped.

Clamping is **NOT** applied during edit mode (`onChange={e => setRaw(...)}`)
— users can type any value, and clamping happens on blur / Enter. This
is deliberate: typing "12" en route to "120" should not jump to the
`max` mid-keystroke.

Edge case: if `current == null` and the user spins, `(null ?? 0)` makes
the starting point `0` — the first spinner click anchors to zero, not
to `min`.

### 3.3 Spinner button enabled / disabled

Each spinner button computes its own disabled state:

| Button | Disabled when |
| --- | --- |
| Increment (▲) | `disabled` OR (`max !== undefined` AND `(current ?? 0) >= max`) |
| Decrement (▼) | `disabled` OR (`min !== undefined` AND `(current ?? 0) <= min`) |

The spinners are `tabIndex={-1}` — they are NOT in the keyboard tab
order. Keyboard users increment / decrement via arrow keys on the
native `type='number'` input (only available during edit mode; see
§4.2).

### 3.4 `step` is NOT a typing constraint

`step` controls only the spinner delta. The native `<input type='number'
step={…}>` attribute is NOT set on the underlying input. Users can
freely type `1.234` even when `step={1}`; the commit will parse
`1.234`, clamp to `[min, max]`, and the host receives `1.234`.

If hosts want step-snapping on commit, they must clamp in their own
`onChange` handler.

### 3.5 `disabled` vs `readonly`

| State | Input editable | Spinners | Visual |
| --- | --- | --- | --- |
| neither | yes | shown + enabled | normal |
| `disabled={true}` | no (native disabled) | shown but disabled | `opacity-50 cursor-not-allowed` on container |
| `readonly={true}` | no (native readOnly) | **hidden** (not rendered) | normal background |

The asymmetry is deliberate: `readonly` represents "this field has a
value the user can see but not edit" — spinners would be confusing.
`disabled` represents "this entire field is currently inactive" —
spinners stay visible (greyed out) to communicate the affordance.

---

## 4. State machine — display ↔ edit transitions

NumericTextBox has two render modes selected by the `editing` flag:

### 4.1 Display mode (`editing === false`)

```
<input
  type="text"
  value={fmt(current, decimals, format)}
  onFocus={enterEditMode}
  ...
/>
```

The input is `type='text'` because:
- Currency strings (`"$1,234.56"`) contain non-numeric characters that
  `type='number'` would reject.
- Percentage strings (`"75%"`) ditto.
- The thousands-separator (`","` from `toLocaleString`) is invalid in
  `type='number'`.

In display mode, the input is read-as-text but still focusable. Native
form submission in display mode submits the **formatted string** as the
value, not the raw number. (See §11 G-NTB1.)

### 4.2 Edit mode (`editing === true`)

```
<input
  type="number"
  value={raw}
  onChange={e => setRaw(e.target.value)}
  onBlur={e => commit(e.target.value)}
  onKeyDown={e => { if (e.key === 'Enter') commit(raw) }}
  ...
/>
```

The input switches to `type='number'`:
- The browser provides numeric keyboard on mobile.
- Native increment / decrement arrow keys work.
- Non-numeric characters are filtered by the browser (locale-dependent).

The `raw` state buffers the in-progress edit. `commit(raw)` is called on
blur OR on Enter, parsing and clamping the result.

### 4.3 Transitions

| Event | From | To | Side effect |
| --- | --- | --- | --- |
| `onFocus` | display | edit | `setEditing(true)`; `setRaw(current != null ? String(current) : '')` |
| `onBlur` | edit | display | `commit(raw)` → parses, clamps, fires `onChange`, `setEditing(false)` |
| Enter key | edit | display | `commit(raw)` — same as blur |
| spinner click | (either) | (unchanged) | `spin(dir)` — direct value mutation; does NOT enter / leave edit mode |

**Important:** clicking a spinner while in edit mode does NOT commit
the raw buffer. The spinner operates on `current` (= the
last-committed value), so:

1. User types `42` into a field showing `10` → `raw = '42'`, `current = 10`.
2. User clicks ▲ → `spin(+1)` → `current` becomes `11`.
3. But the input is still in edit mode showing `raw = '42'`.

This is a known confusion point (§11 G-NTB3).

### 4.4 Commit pipeline

```
commit(str: string):
  n = parseFloat(str)            // 'abc' → NaN; '' → NaN; '12.3' → 12.3
  next = isNaN(n) ? null : clamp(n)
  if (value === undefined)
    setInternal(next)            // uncontrolled — update local state
  onChange?.(next)
  setEditing(false)
```

`parseFloat` is lenient:
- `'12.3abc'` parses as `12.3` (parseFloat reads as much as it can).
- `'1e3'` parses as `1000` (scientific notation).
- `' 42 '` parses as `42` (leading whitespace).

This may or may not match host expectations. Hosts who need strict
parsing should validate in their `onChange` handler.

---

## 5. Format strings — `fmt()` vocabulary

`fmt(v, decimals, format)` returns the display string:

```typescript
function fmt(v: number | null | undefined, decimals: number, format?: string): string {
  if (v == null) return ''
  if (format) {
    if (/^c/.test(format))
      return v.toLocaleString('en-US', { style: 'currency', currency: 'USD',
        minimumFractionDigits: parseInt(format.slice(1)) || 2 })
    if (/^p/.test(format))
      return (v * 100).toFixed(parseInt(format.slice(1)) || 0) + '%'
  }
  return v.toFixed(decimals)
}
```

| Format | Example input | Output |
| --- | --- | --- |
| _unset_ (`decimals=2`) | `1234.5` | `'1234.50'` |
| `'c'` | `1234.5` | `'$1,234.50'` (default 2 decimals) |
| `'c0'` | `1234.5` | `'$1,235'` |
| `'c4'` | `1234.5` | `'$1,234.5000'` |
| `'p'` | `0.75` | `'75%'` (default 0 decimals) |
| `'p2'` | `0.075` | `'7.50%'` |

### 5.1 Locale and currency limitations

- Currency is **hard-coded USD** with `en-US` locale. There is no prop
  to select EUR / GBP / JPY or other locales.
- Percentage uses `.toFixed`, not `toLocaleString` — no thousands
  separator for very large percentages.
- Format codes are case-sensitive (`'C2'` does NOT match `/^c/`).
- Any format string starting with `'c'` or `'p'` is parsed; format
  strings starting with other letters are silently ignored (the
  fallback `toFixed(decimals)` is used).

This format-code vocabulary is intentionally minimal. Apps with
multi-currency or multi-locale needs should NOT use NumericTextBox in
display mode for currency — they should format outside and pass a
plain numeric input, or use a dedicated CurrencyField (catalog row #37).

---

## 6. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onChange` | `(v: number \| null)` | (a) blur from edit mode — commits the raw buffer; (b) Enter key in edit mode — same as blur; (c) spinner click — applies `step`. **Not** fired on per-keystroke typing. |

Native React events (`onFocus`, `onBlur`, `onKeyDown`) are intercepted
internally and NOT forwarded. Hosts cannot pass through their own
`onFocus` / `onBlur` handlers in M1.

---

## 7. Slots

NumericTextBox has **no slot extensibility** in M1:
- No prefix slot (e.g. for a `$` icon — embedded in the format string).
- No suffix slot.
- No replacement of the spinner icons.

The visual spinner glyphs are hard-coded `▲` / `▼` Unicode characters.
PAO Styling owns the icon-component migration.

---

## 8. Form integration

- **`name` attribute.** The underlying `<input>` participates in native
  form submission. The submitted value depends on the current mode:
  - Display mode → submits the **formatted string** (e.g. `"$1,234.56"`).
  - Edit mode → submits the **raw string** (e.g. `"1234.56"`).
- This asymmetry is a known issue — see §11 G-NTB1. Hosts who use
  NumericTextBox in real form submission should either submit
  programmatically (`onChange` writes to a hidden field) or always
  force the field to blur before submit.
- **Browser validation.** `type='number'` validation applies only in
  edit mode; `type='text'` mode bypasses HTML5 numeric validation
  entirely.

---

## 9. Component composition

- **General numeric entry.** Use NumericTextBox for quantities, counts,
  ratios, financial figures (USD only).
- **Currency entry — multi-currency.** Use the dedicated `CurrencyField`
  (catalog #37) — NumericTextBox's hard-coded USD locale is
  insufficient.
- **Percentage entry.** NumericTextBox + `format='p2'` is canonical.
- **Integer-only entry.** Set `decimals={0}` AND validate / round in
  `onChange` (NumericTextBox does not refuse decimal typing).
- **Bounded slider-like entry.** Pair NumericTextBox with `Slider`
  (catalog #115) for visual + precise dual control.

---

## 10. Open questions (for council)

1. **Form-submit asymmetry (G-NTB1).** Display-mode submits the
   formatted string. Should NumericTextBox render a hidden synced
   `<input type='hidden' name={name} value={String(current)} />` so
   the form-submit value is always the raw number?
2. **Spinner during edit mode (G-NTB3).** Should a spinner click while
   editing commit the raw buffer first, then spin? Or commit-and-cancel
   the edit? Today it spins from the last-committed value.
3. **Locale / currency.** Should `format` accept a richer
   `Intl.NumberFormatOptions` object instead of a 2-char code?
4. **Step snapping.** Should commit snap to the nearest multiple of
   `step` from `min`?
5. **`onFocus` / `onBlur` passthrough.** Should NumericTextBox forward
   these events to the host AFTER its internal handling?

---

## 11. Deferred features

- **Multi-locale / multi-currency formatting.**
- **Intl.NumberFormat composition.**
- **Group separators in non-format mode.**
- **Negative-number visual treatment** (parentheses, red).
- **Scientific-notation display.**
- **Increment with modifier keys** (Shift+▲ for step×10, etc.).
- **`onChange` per-keystroke variant.**

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-NTB1 | Critical | Native form-submit value differs between display mode (formatted string) and edit mode (raw string) | [OPEN 2026-06-06] §10.1 council question — hidden synced input proposed |
| G-NTB2 | High | `parseFloat` leniency (`'12abc'` → `12`) may not match host expectations | [ACCEPTED-RISK 2026-06-06] §4.4 documents; hosts validate in `onChange` |
| G-NTB3 | High | Spinner click during edit mode operates on last-committed `current`, ignoring `raw` buffer | [OPEN 2026-06-06] §4.3 + §10.2 — commit-then-spin under consideration |
| G-NTB4 | High | Hard-coded USD currency + `en-US` locale | [ACCEPTED-RISK 2026-06-06] §5.1 — multi-currency hosts use `CurrencyField` (catalog #37) |
| G-NTB5 | Medium | `flat` fillMode does NOT auto-override `rounded` (divergence from Input) | [OPEN 2026-06-06] family-wide vocabulary pin needed |
| G-NTB6 | Medium | `step` is spinner-only; not enforced for keyboard typing | [ACCEPTED-RISK 2026-06-06] §3.4 documents; hosts snap in `onChange` |
| G-NTB7 | Medium | `onFocus` / `onBlur` not forwarded to host | [OPEN 2026-06-06] §10.5 council question |
| G-NTB8 | Low | Spinner glyphs are hard-coded Unicode (`▲` / `▼`) | [ACCEPTED-RISK 2026-06-06] PAO Styling owns icon-component migration |
| G-NTB9 | Low | `null` vs `0` data-model invariant is not type-enforced beyond `number \| null` | [ACCEPTED-RISK 2026-06-06] §3.1 documents host discipline |
