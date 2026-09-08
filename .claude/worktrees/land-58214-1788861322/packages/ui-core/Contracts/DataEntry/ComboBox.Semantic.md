# ComboBox — Semantic Contract

- **Component:** ComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ComboBox.Interaction.md) · [Accessibility](./ComboBox.Accessibility.md) · [Styling](./ComboBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ComboBox.tsx`
- **Catalog row:** #32 ComboBox (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled listbox + custom popover `<div>` (no Radix primitive)

---

## 1. Component purpose

**ComboBox** — a standalone select-or-type widget. Combines a text input with a filterable dropdown. Unlike ComboBoxField, it does not wrap inside a FormField context (no label/hint/error). Use ComboBoxField when you need field-level form integration.

---

## 2. Props

```typescript
interface ComboBoxItem {
  text: string
  value: string | number
  disabled?: boolean
  [key: string]: unknown
}

interface ComboBoxProps {
  value?: string | number | null
  defaultValue?: string | number
  onValueChange?: (value: string | number | null) => void
  data: ComboBoxItem[] | string[]
  textField?: string           // default: 'text' — key to extract display text
  valueField?: string          // default: 'value' — key to extract value
  placeholder?: string         // default: 'Select or type...'
  disabled?: boolean           // default: false
  loading?: boolean            // default: false
  allowCustom?: boolean        // prop exists but not yet implemented
  filterable?: boolean         // default: true
  size?: 'small' | 'medium' | 'large'             // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'         // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full' // default: 'medium'
  id?: string
  name?: string
  className?: string
}
```

### 2.1 Appearance props

| Prop | Value | Visual effect |
|---|---|---|
| `fillMode` | `'solid'` (default) | Filled background matching the theme surface color |
| `fillMode` | `'outline'` | Transparent background with a visible border only |
| `fillMode` | `'flat'` | No border and no background — minimal appearance |
| `rounded` | `'small'` | `border-radius` tight token (~2 px) |
| `rounded` | `'medium'` (default) | `border-radius` standard token (~4 px) |
| `rounded` | `'large'` | `border-radius` loose token (~8 px) |
| `rounded` | `'full'` | `border-radius: 9999px` — pill shape |

`size` (`'small' | 'medium' | 'large'`) controls input height and font size independently of `fillMode` and `rounded`. All three appearance axes are orthogonal and can be combined freely.

---

## 3. Data normalization

String items are normalized to `{ text: item, value: item }`. Object items use `textField`/`valueField` to extract display text and value. Extra properties are preserved but not rendered.

---

## 4. Controlled / uncontrolled

Controlled when `value` is provided; internal state (`internal`) tracks uncontrolled mode. `onValueChange` fires the raw `value` field (string or number), never the full item object.

---

## 5. Filter behavior

When `filterable=true` and the user types, `filter` state drives case-insensitive substring matching against `item.text`. When closed or blurred, `displayText` (the selected item's label) is shown instead.

---

## 6. Editing vs display mode

`editing=true` when the input is focused — shows `filter` text. `editing=false` when blurred — shows `displayText` (selected item label) or empty string.

---

## 7. Known gaps

Canonical table — companion contracts cross-reference these IDs.

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CBX1 | High | `aria-controls` missing on input — AT cannot identify the listbox; WCAG SC 4.1.2 (Level A) violation | Blocking-before-v1-ship — authoritative disposition per ComboBox.Accessibility.md §2 |
| G-CBX2 | High | `aria-activedescendant` missing on input — active item not announced on ArrowDown/Up; WCAG SC 4.1.2 (Level A) violation | Blocking-before-v1-ship — authoritative disposition per ComboBox.Accessibility.md §2 |
| G-CBX3 | Medium | `aria-autocomplete="list"` missing on input | Accepted-risk M1 |
| G-CBX4 | Low | `allowCustom` prop exists in interface but is not implemented — custom values are silently ignored | Accepted-risk M1 — tracked in ComboBox.Interaction.md §5 |
| G-CBX5 | Medium | Toggle button has no `aria-label` — AT reads it as `'▾'` or `'⟳'` | Accepted-risk M1 |
| G-CBX6 | Low | No `aria-label` or `aria-labelledby` on input — callers must supply external `<label>` | Accepted-risk M1 |

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** ComboBox ~50% Kendo-minimum coverage. P1 misses: loading state, `required`, `aria-controls`, `aria-activedescendant`, `onInputChange`.

> **Note:** ComboBox is the **primitive** (no FormField wrapper). ComboBoxField expansion is in its own
> Semantic contract. These expansions target the primitive surface only.

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `ComboBoxProps`:

```typescript
required?: boolean   // default: false — aria-required="true" on the <input>
error?: boolean      // already present (boolean only); no change
```

`error` is already present as a boolean. No `validationMessage` prop — message composition
is via `FormField` / `ValidationMessage` per FR-1 §3.

`required` drives `aria-required="true"` on the text `<input>`. The FormField asterisk is
driven by `FormFieldContext.required` when ComboBox is used inside a FormField.

**Closes:** missing `required` prop (audit row).

**wave-E1**

---

### §E1-2 FR-2 adoption — focus + popup events (family ruling FR-2)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `ComboBoxProps`:

```typescript
onFocus?: React.FocusEventHandler<HTMLInputElement>
onBlur?: React.FocusEventHandler<HTMLInputElement>
open?: boolean                         // controlled popup state
onOpenChange?: (open: boolean) => void // Radix-style canonical pair
```

`open` / `onOpenChange` adopt the FR-2 canonical pair. The existing internal open/close
state machine becomes the uncontrolled default when `open` is omitted.

**wave-E1**

---

### §E1-3 FR-3 size-vocabulary migration (family ruling FR-3)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-3.

`size` values migrate from `'small' | 'medium' | 'large'` → `'sm' | 'md' | 'lg'`.
`rounded` migrates from `'small' | 'medium' | 'large' | 'full'` → `'sm' | 'md' | 'lg' | 'full'`.
Deprecation aliases accepted at runtime (console.warn in dev); removed at next major.

**wave-E1**

---

### §E1-4 Loading state with popup suppression (closes G-SF7 analog / audit P1)

Add to `ComboBoxProps`:

```typescript
loading?: boolean   // default: false
```

**Semantics:**

1. When `loading={true}`: a spinner appears in the trailing slot (replaces the toggle chevron).
   The spinner MUST animate (`animate-spin` or shared `<Spinner>` component).
2. `aria-busy="true"` is set on the root wrapper `<div>`.
3. **Popup suppression:** the dropdown listbox MUST NOT open while `loading={true}`. The toggle
   button is `disabled`; keyboard ArrowDown is a no-op. If `open={true}` is passed while loading,
   the popup is forced closed (loading wins).
4. `data` array is not emptied during loading — hosts who want a blank list set `data={[]}`.

**wave-E1**

---

### §E1-5 `onInputChange` for server-side filtering (closes audit P1)

Add to `ComboBoxProps`:

```typescript
onInputChange?: (value: string) => void
```

Fires on every keystroke in the filter input (no debounce — debounce is the host's responsibility).
When provided, `filterable` should typically be `false` because the host controls the `data` array
externally (server-filtered results). The prop can coexist with `filterable={true}` for hybrid
client+server flows, but this is unusual.

**Server-side pattern:**

```tsx
const [query, setQuery] = useState('')
const { data: items } = useQuery(['items', query], () => fetchItems(query), { keepPreviousData: true })

<ComboBox
  data={items ?? []}
  filterable={false}
  onInputChange={setQuery}
  loading={isFetching}
  ...
/>
```

**wave-E1**

---

### §E1-6 ARIA combobox — mandatory keyboard/ARIA rows (WCAG Level A; closes G-CBX1/G-CBX2)

These rows are P1 blockers from the audit (G-CBX1/G-CBX2 are WCAG SC 4.1.2 Level A violations).
Authoritative disposition lives in `ComboBox.Accessibility.md §2`; this section is the semantic
prop contract that enables the fix.

**Required props/attributes (no new JS-facing props — these are implementation obligations):**

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `aria-controls` | `<input role="combobox">` | `"{id}-listbox"` | G-CBX1 |
| `aria-activedescendant` | `<input role="combobox">` | `"{id}-option-{index}"` of the highlighted item, or `undefined` when no item highlighted | G-CBX2 |
| `role="listbox"` | Popup container `<div>` | literal | (already required — confirm present) |
| `id="{id}-listbox"` | Popup container `<div>` | literal | matches aria-controls |
| `role="option"` | Each list item `<div>` | literal | (already required) |
| `id="{id}-option-{index}"` | Each list item | literal | matches aria-activedescendant |
| `aria-selected="true/false"` | Each list item | boolean | WAI-ARIA combobox 1.2 |

`id` on the root element is required for this wiring. If `id` is not provided by the host,
auto-generate via `useId()`.

**wave-E1** — WCAG Level A, mandatory before v1-ship.

---

### §E1-7 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| Virtual scrolling | wave-E2+ |
| `groupField` / item grouping | wave-E2+ |
| `itemRender` / `valueRender` custom slots | wave-E2+ |
| `header` / `footer` popup slots | wave-E2+ |
| `popupSettings` | wave-E2+ |
| Adaptive mode | wave-E2+ |
| `clearButton` | wave-E2+ |
| `allowCustom` (freeform entry; G-CBX4) | wave-E2+ |
| `suggest` (inline autocomplete) | wave-E2+ |

---

## FormField context integration (Cohort-2, 2026-06-12)

ComboBox reads `FormFieldContext` via `useFormField()`. When wrapped in a `<FormField>`, the following props are automatically applied:

| Context value | Activates when | Prop precedence |
|---|---|---|
| `describedBy` | FormField has `hint` or `error` | No prop override — context-only |
| `required` | FormField has `required={true}` | Prop `required` wins if explicitly set |
| `disabled` | FormField has `disabled={true}` | Prop `disabled` wins if explicitly set |

**Unified data model (Cohort-2):** ComboBox now accepts both data models:

- `data: ComboBoxItem[]` — the original rich model (label, value, disabled)
- `options: { value: string; label: string }[]` — the simple model from ComboBoxField

At least one must be provided. When `options` is used, it is mapped to `ComboBoxItem[]` internally via `optionsToItems()`.

**Standalone (no FormField):** `useFormField()` returns an empty object; no change to existing standalone behaviour.

**Inside FormField:**
```tsx
<FormField name="category" label="Category" required>
  <ComboBox name="category" options={opts} />
</FormField>
```
ComboBox automatically gains `aria-required="true"` and `aria-describedby` wired to the FormField hint/error elements.

**Prop-overrides-context rule:** `resolved = prop ?? contextValue ?? false`.

**Replaces:** ComboBoxField (deprecated shim). See [ComboBoxField.Semantic.md](./ComboBoxField.Semantic.md) for migration guidance.
