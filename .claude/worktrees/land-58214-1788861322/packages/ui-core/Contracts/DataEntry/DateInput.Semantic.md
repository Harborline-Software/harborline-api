# DateInput — Semantic Contract

- **Component:** DateInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DateInput.Interaction.md) · [Accessibility](./DateInput.Accessibility.md) · [Styling](./DateInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateInput.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input type="text">` with hand-rolled date parsing

---

## 1. Component purpose

**DateInput** — a thin wrapper around `<input type="date">` that manages Date object ↔ YYYY-MM-DD string conversion. No calendar picker UI; uses the browser's native date input. For a custom calendar picker, use DatePicker.

---

## 2. Props

```typescript
interface DateInputProps {
  value?: Date | null
  defaultValue?: Date
  onChange?: (date: Date | null) => void
  format?: string           // prop exists but not used — native input controls format
  min?: Date
  max?: Date
  disabled?: boolean        // default: false
  readonly?: boolean        // default: false
  placeholder?: string
  size?: 'small' | 'medium' | 'large'             // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'         // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full' // default: 'medium'
  id?: string
  name?: string
  className?: string
}
```

---

## 3. Date conversion

`toInputValue(d)` converts a `Date` object to `YYYY-MM-DD` string for the native input. On change, the native string `e.target.value` is parsed as `new Date(e.target.value + 'T00:00:00')` (local time, avoiding UTC midnight shift). Empty string → `null`.

---

## 4. format prop

The `format` prop is declared but not implemented. The native `<input type="date">` controls display format per browser locale. This is an M1 accepted limitation.

---

## 5. Controlled / uncontrolled

Controlled when `value` is provided. Uncontrolled when `value` is omitted (`defaultValue` seeds internal state).

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (validation — required + error), FR-2 (focus events), FR-3 (appearance axes — size vocabulary migration).
> Supersedes: `size` vocabulary normalizes from `'small'|'medium'|'large'` to `'sm'|'md'|'lg'` per FR-3; deprecation aliases retained at Wave-N, removed at Wave-N+2.

### 6. Additional props

```typescript
// Added in Wave-N
interface DateInputPropsExpansion {
  // FR-2: focus events
  onFocus?: React.FocusEventHandler<HTMLInputElement>
  onBlur?: React.FocusEventHandler<HTMLInputElement>

  // FR-1: validation
  required?: boolean    // sets aria-required="true"; drives FormField asterisk via FormFieldContext

  // ARIA linking (resolves audit gaps G-DI3 and ariaDescribedBy/ariaLabelledBy)
  ariaLabel?: string
  ariaDescribedBy?: string
  ariaLabelledBy?: string

  // format + spinners/steps (resolves audit P1 gaps)
  // NOTE: these are contract-reserved for the Wave-N custom segmented-editor path.
  // The native <input type="date"> M1 path does not implement them; they are carried
  // forward as reserved until the segmented-editor wave.
  spinners?: boolean                          // show up/down spin buttons per segment
  steps?: {
    year?: number
    month?: number
    day?: number
  }                                           // per-segment increment amounts
  formatPlaceholder?: string | {
    day?: string
    month?: string
    year?: string
  }                                           // per-segment hint text

  // FR-3: canonical size aliases (deprecation aliases for 'small'|'medium'|'large')
  // size?: 'sm' | 'md' | 'lg' | 'small' | 'medium' | 'large'  -- see Styling contract
}
```

### 7. required (FR-1)

`required` prop sets `aria-required="true"` on the native `<input>` and informs the host `FormFieldContext` that the field is required. Validation messages remain composed via `FormField / ValidationMessage` — DateInput does not render its own error message. See FR-1 §3.

### 8. onFocus / onBlur (FR-2)

`onFocus` and `onBlur` are forwarded directly to the native `<input>` element. They surface the native `FocusEvent` unchanged.

### 9. ARIA linking (resolves G-DI3)

`ariaDescribedBy` → `aria-describedby`. `ariaLabelledBy` → `aria-labelledby`. `ariaLabel` → `aria-label` (fallback when no visible label is present). These resolve gaps G-DI3 (Interaction) and the audit's ariaDescribedBy/ariaLabelledBy misses.

### 10. Spinners / steps / formatPlaceholder (contract-reserved)

These three props are carried as contract surface now so hosts can wire them without a contract amendment when the segmented-editor wave ships. In the M1 native-input path they are no-ops. Implementations MUST NOT silently drop them — the component should pass `spinners/steps/formatPlaceholder` through to the segmented editor when that path is active.
