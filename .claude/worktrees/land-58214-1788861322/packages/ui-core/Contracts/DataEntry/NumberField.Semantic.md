# NumberField — Semantic Contract

- **Component:** NumberField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./NumberField.Interaction.md) · [Styling](./NumberField.Styling.md) · [Accessibility](./NumberField.Accessibility.md)
- **Related contract:** [FormField.Semantic.md](./FormField.Semantic.md) — typical wrapper for label + hint + error.
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberField.tsx`
- **Catalog row:** #90 NumericTextBox — NumberField is the FormField-family controlled wrapper for numeric input; see MG-2 for full spec alignment (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input type="number">` with FormFieldContext wrapper

---

## 1. Purpose

NumberField is the M1 numeric-input control of `@harborline-software/ui-react`. It is a
thin controlled wrapper around a native `<input type="number">` — leveraging
the browser-provided up/down spinners, keyboard arrow-key increment, and
mobile numeric keypad. Currency, percentage, and unit-bearing inputs are
deferred to a richer future component.

---

## 2. Data model

NumberField is fully controlled. The `value` is `number | string` because the
native `<input type="number">` permits an intermediate empty-or-partial state
that doesn't round-trip through `Number` (e.g. mid-typing `"1.2e"`).

```typescript
interface NumberFieldProps {
  name: string
  value: number | string
  onChange: (v: string) => void
  step?: number
  min?: number
  max?: number
  disabled?: boolean
  error?: boolean
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | `string` | _required_ | Field name. Drives `id` and `name` on the native input. |
| `value` | `number \| string` | _required_ | Controlled value. Hosts that own a numeric model may pass a `number`; hosts that allow intermediate string states (mid-typing) may pass a `string`. |
| `onChange` | `(v: string) => void` | _required_ | Fires on every change (keystrokes, spinner clicks, paste, arrow-key increment). Payload is **always `string`** — the raw `e.target.value`, not a parsed number. This is a **live/typing callback** — **no commit-on-blur callback exists in M1.** Hosts that need commit semantics for monetary or formula fields must observe the native `blur` event via a custom wrapper. The host is responsible for parsing and validating. |
| `step` | `number` | — | Native step attribute. Controls arrow-key / spinner increment. Omit for unrestricted (1 default in most browsers). |
| `min` | `number` | — | Native lower bound. Spinner stops at `min`; typing below `min` flags `validity.rangeUnderflow` on submit. |
| `max` | `number` | — | Native upper bound. Symmetric to `min`. |
| `disabled` | `boolean` | `false` | Disables the input. |
| `error` | `boolean` | `false` | Renders the error treatment + sets `aria-invalid={true}`. |

### 3.1 Why `onChange` returns `string`

Returning `string` lets the host distinguish between "user is mid-typing" (e.g.
`""`, `"-"`, `"1."`, `"1e"`) and "user has committed a valid number." Forcing
a `number` payload would either silently drop intermediate states or require
NaN handling at every consumer.

Hosts that want a `number` parse at every keystroke:

```typescript
<NumberField
  name="qty"
  value={qty}
  onChange={(v) => setQty(v === '' ? '' : Number(v))}
/>
```

### 3.2 No internal parsing or clamping

NumberField does **not** parse the string, does **not** clamp to `min`/`max`,
and does **not** round to `step`. All of those are host or
form-submission-time concerns. The native browser handles spinner-bound
enforcement; typed input is unrestricted.

#### 3.2.1 `step` + `min` interaction (closes G-NF4)

When both `step` and `min` are set, the spinner's increment sequence starts
at `min`, not at `0`:

```
min=0.5, step=1  →  spinner goes 0.5 → 1.5 → 2.5 → ...
min=0,   step=1  →  spinner goes 0 → 1 → 2 → ...
```

This is native browser behavior. Hosts expecting integer increments from 0
when `min > 0` must set `min=0` and validate the range separately.

#### 3.2.2 Recommended `value` type for typed-input fields (closes G-NF5)

The `value` prop accepts `string | number`. **Use `string` for fields the
user types into; use `number` only for programmatically-set values.**

```tsx
// Recommended — typing field:
const [qty, setQty] = useState('')
<NumberField value={qty} onChange={setQty} />

// OK — programmatic only (never user-typed):
<NumberField value={42} onChange={...} />

// Avoid mixing — React will warn when value flips between
// controlled string and controlled number:
const [v, setV] = useState<string | number>('')  // confusing
```

React's `valueAsNumber` on the DOM event is NOT used by NumberField (it
returns `NaN` for empty/invalid input). The `onChange` callback always
delivers `e.target.value` (string).

### 3.3 Known footguns

Two browser-native behaviors of `<input type="number">` affect NumberField and
are not mitigated by the component:

**Mouse-wheel scroll increments the value.** When NumberField is focused, a
mouse-wheel event increments or decrements the native number input. This silently
changes the value when a user scrolls the page with the field still focused — a
common data-entry bug. Hosts should suppress this behavior by passing a custom
`onWheel` handler via an HTML attribute wrapper:
```typescript
// In the host form component:
<div onWheel={(e) => (e.target as HTMLInputElement).blur()}>
  <NumberField ... />
</div>
```
(NumberField does not currently accept `onWheel` directly — see §3.4 passthrough
gap. A native `onWheel={e => e.currentTarget.blur()}` approach may be added in M1.1.)

**Locale decimal separator.** The browser submits `e.target.value` using the
user's locale decimal separator (`.` in en-US, `,` in de-DE). `Number('1,5')`
returns `NaN` in an en-US browser. Hosts that parse the `onChange` payload must
account for cross-locale separators:
```typescript
// Safe cross-locale parse:
const n = parseFloat(v.replace(',', '.'))
```
Or use `Intl.NumberFormat` for locale-aware formatting. This is a native browser
behavior that NumberField inherits and does not normalize.

### 3.4 HTML attribute passthrough

Not implemented. Known gap. (Especially impactful here — `inputMode`,
`pattern`, `autoComplete="off"`, `enterKeyHint` are all useful for numeric
input.)

### 3.5 FormField integration

Same pattern as TextField — reads `describedBy` from `useFormField()` and
threads it onto `aria-describedby`.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onChange` | `(v: string)` | Any change event from the native input: typing, paste, arrow-key step, spinner click. Payload is the raw `e.target.value` (string). |

---

## 5. Slots

No slot extensibility in M1. No prefix / suffix slots for currency symbols or
units; no inline label adornment. All deferred (§7).

---

## 6. Component composition

- **FormField wrapping (canonical).** Same as TextField.
- **Standalone usage.** Permitted.
- **Form-element parenting.** Native `<input>` participates in form
  submission via `name`.

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Prefix / suffix slots** — currency symbols (`$`), units (`kg`, `%`).
  High-demand in property-management / ERP UI; expect this in fast-follow.
- **Locale-aware formatting** — thousands separators, decimal-point
  swaps. Native input doesn't format displayed text; a custom widget
  would.
- **Currency mode** — `value` as cents/minor units, display as major-unit
  with symbol.
- **Percentage mode** — `value` as decimal (0.05), display as percent
  (5%).
- **Big-integer / BigInt support** — out of scope (use string-mode).
- **`size` variants** — NumberField does not expose `size` in M1.
- **HTML attribute passthrough.**

---

## 8. Open questions (for council)

1. **`onChange` payload type.** `string` is the M1 choice. Alternative:
   `string | number` (with `string` only when value is non-numeric). Or:
   a `{ raw: string; parsed: number | null }` shape. Confirm `string`
   is the right baseline.
2. **Prefix / suffix slots.** Currency entry needs this almost
   immediately. M1 or fast-follow? (Leaning: fast-follow — define the
   slot shape now, ship in M1.1.)
3. **Step granularity.** Should the default `step` be `1` or unspecified?
   Unspecified lets users type decimals freely; `1` constrains the
   arrow-key increment but disallows fractional input in some browsers.

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
| G-NF1 | Critical | Commit semantics vs. typing not disambiguated | [RESOLVED 2026-06-05] §3 onChange row: fires on every change including spinner, paste, arrows; no commit-on-blur |
| G-NF2 | Critical | Wheel-scroll-changes-value not addressed | [RESOLVED 2026-06-05] §3.3 "Known footguns": wheel increments native input; suppress via `onWheel={e=>e.target.blur()}` |
| G-NF3 | Critical | Locale decimal-separator handling missing | [RESOLVED 2026-06-05] §3.3: `e.target.value` is locale string; `Number("1,5")` returns NaN in en-US; use `parseFloat` with replace |
| G-NF4 | High | `step` + `min` interaction | [RESOLVED 2026-06-05] §3.2.1: spinner goes `min + n×step`, not `0 + n×step` |
| G-NF5 | High | `value` mid-typing intermediate states | [RESOLVED 2026-06-05] §3.2.2: use `string` for user-typed fields; `number` for programmatic only |
| G-NF6 | Medium | `valueAsNumber` NaN gotcha | [ACCEPTED-RISK 2026-06-05] §3.3 note added |
| G-NF7 | Medium | SelectOnFocus (Telerik default) parity absent | [ACCEPTED-RISK 2026-06-05] Current behavior: no select on focus; host adds via `onFocus` if needed |
