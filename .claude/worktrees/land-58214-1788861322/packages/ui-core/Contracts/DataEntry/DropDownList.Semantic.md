# DropDownList — Semantic Contract

- **Component:** DropDownList
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DropDownList.Interaction.md) · [Accessibility](./DropDownList.Accessibility.md) · [Styling](./DropDownList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownList.tsx`
- **Catalog row:** #48 DropDownList (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled listbox + native `<input>`

---

## 1. Component purpose

**DropDownList** is the canonical single-select dropdown of
`@harborline-software/ui-react`. The user picks **one** value from a closed list of
options; free-text entry is NOT supported (that is `ComboBox`).

The implementation uses a deliberate **invisible-native-select** pattern:

- A native HTML `<select>` is positioned `absolute inset-0` with
  `opacity-0`. It is fully interactive and receives all user input.
- A visible `<span>` displays the selected item's text, layered above
  the select with `pointer-events-none`.
- The visible chevron (`▾`) and loading spinner (`⟳`) are decorative
  only.

This pattern is **a11y-first by design**: the native `<select>` provides
- Full keyboard navigation (Up / Down / Home / End / typeahead).
- Native screen-reader announcements.
- Native mobile picker UIs (iOS wheel, Android dialog).
- Native focus ring + outline behaviour.

DropDownList is consciously *not* a Radix / floating-UI custom popover —
that path trades correctness for visual flexibility, and Harborline has
chosen correctness. Hosts that need richer item rendering (icons per
option, multi-line items, grouping with separators, etc.) should use
`ComboBox` or `MultiSelect`, both of which use a custom popover.

---

## 2. Data model

```typescript
interface DropDownListItem {
  text: string
  value: string | number
  disabled?: boolean
  [key: string]: unknown   // extra fields tolerated (textField / valueField can point at them)
}

interface DropDownListProps {
  // Value
  value?: string | number | null
  defaultValue?: string | number
  onValueChange?: (value: string | number | null) => void

  // Data + field mapping
  data: DropDownListItem[] | string[]
  textField?: string          // default: 'text'
  valueField?: string         // default: 'value'
  defaultItem?: DropDownListItem | string

  // States
  placeholder?: string        // default: 'Select...'
  disabled?: boolean          // default: false
  loading?: boolean           // default: false

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

Internal state is minimal: a single `internal: string | number | null`
for uncontrolled mode. Open/closed state is owned by the browser (the
native `<select>` decides when its dropdown is visible) — DropDownList
does not track it.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `value` | `string \| number \| null` | — | Controlled selected value. `null` represents "no selection" — the placeholder is shown. When `undefined`, DropDownList is uncontrolled. |
| `defaultValue` | `string \| number` | — | Initial selection for uncontrolled mode. No `null` form (uncontrolled "no initial selection" is expressed by omitting the prop). |
| `onValueChange` | `(v: string \| number \| null) => void` | — | Selection-change callback. Fires when the user picks an option. Payload is `string` (native selects always return strings) unless the selection is the placeholder, in which case payload is `null`. See §3.4 for the number-coercion caveat. |
| `data` | `DropDownListItem[] \| string[]` | _required_ | Source array. Strings normalize to `{ text: s, value: s }`. Objects use `textField` / `valueField` for extraction. See §4. |
| `textField` | `string` | `'text'` | Property name on each item used as the visible label. |
| `valueField` | `string` | `'value'` | Property name on each item used as the value. |
| `defaultItem` | `DropDownListItem \| string` | — | Optional item prepended to the list. Typically represents an "all" / "none" / "any" pseudo-option. See §5. |
| `placeholder` | `string` | `'Select...'` | Text rendered in the `<option value="">` placeholder slot AND in the visible span when nothing is selected. |
| `disabled` | `boolean` | `false` | When `true`, the native `<select>` is disabled — entire control non-interactive. |
| `loading` | `boolean` | `false` | When `true`: the visible label is replaced with `"Loading..."`, the chevron is replaced with a spinner `⟳`, AND the underlying `<select>` is `disabled`. See §3.5. |
| `size`, `fillMode`, `rounded` | — | — | Same vocabulary as Input. Apply to the container. |
| `id`, `name`, `className` | — | — | Standard. `name` participates in native form submission. |

### 3.1 Selection-only — no free-text entry

DropDownList renders an HTML `<select>` — by definition the user cannot
type a custom value. For free-text entry from a list of suggestions,
use `ComboBox` (which renders an `<input>` + custom popover).

### 3.2 Single-select only

DropDownList does NOT support `multiple` selection. The underlying
`<select>` is rendered without the `multiple` attribute. For multi-select
use `MultiSelect` (catalog #82) — a separate component with a custom
popover.

### 3.3 `null` semantics

A core data-model invariant — the value triad:

| `value` prop | Meaning | Visible label | `onValueChange` payload on placeholder click |
| --- | --- | --- | --- |
| `undefined` | Uncontrolled (no controlled selection) | Falls back to `internal` state | — |
| `null` | Controlled, no selection | `placeholder` text | n/a (already null) |
| `''` (string) | Controlled, value-of-empty-string | Same as `null` in display (matches the placeholder option's `value=""`) | n/a |
| `'X'` / `42` | Controlled, value `'X'` / `42` | The matching item's `text` (or `placeholder` if no match) | — |

The empty-string and `null` cases are intentionally collapsed:
`current ?? ''` is passed to the native `<select value=…>`, so a `null`
controlled value renders the empty `<option value="">` — matching the
placeholder.

### 3.4 Number-coercion caveat

Native `<select>` only emits **strings** via `e.target.value` — even
when the `<option value={42}>` is set with a number, the DOM returns
`'42'`.

Consequences:

- If `data` contains numeric `value` fields (`{ value: 42, text: 'Forty-two' }`)
  and the host selects "Forty-two", `onValueChange` fires with `'42'`
  (string), NOT `42` (number).
- The comparison `items.find(i => i.value == current)` in the display
  span deliberately uses **loose equality** (`==`) to bridge the
  string ↔ number gap — `42 == '42'` is `true`, so the matching item's
  text is found regardless of whether the host stores the value as a
  number or string.
- Hosts that need typed-number selection must coerce in their
  `onValueChange` handler: `onValueChange={v => host(v == null ? null : Number(v))}`.

### 3.5 `loading` state

`loading={true}` does three things simultaneously:

1. The visible label is replaced with the literal string `"Loading..."`.
2. The chevron is replaced with the spinner glyph `⟳` (no animation in
   the current implementation — see §11 G-DDL3).
3. The underlying `<select>` is `disabled` (via `disabled || loading`).

The `data` array is NOT replaced or hidden during loading — if a
selection exists, it remains in the DOM but is masked by the
`"Loading..."` label. Hosts who want to show an empty placeholder
while loading should set `data={[]}` themselves.

`loading` does NOT show a transient indicator inside the dropdown — the
dropdown is simply unopenable while loading.

---

## 4. Data normalization

The `normalizeItem(item, textField, valueField)` helper handles three
input shapes:

| Input | Normalized output |
| --- | --- |
| `'Banana'` (string) | `{ text: 'Banana', value: 'Banana' }` |
| `{ text: 'Banana', value: 'B' }` | `{ text: 'Banana', value: 'B', disabled: undefined }` |
| `{ label: 'Banana', id: 'B' }` with `textField='label'`, `valueField='id'` | `{ text: 'Banana', value: 'B' }` |

The normalizer preserves `disabled` from object items but drops other
extra fields when constructing the output. The implementation uses
`item[textField] ?? item.text` so an object missing the configured field
falls back to `.text` — this is convenient but can mask configuration
bugs (passing `textField='label'` against data with `.text` silently
uses `.text`).

Normalization runs on **every render** (no memoization). For large
`data` arrays (>500 items) this may become measurable; not optimised in
M1. Hosts with very large lists should consider `ComboBox` with
virtualization.

---

## 5. `defaultItem`

When provided, `defaultItem` is normalized and **prepended** to the
options list. It is rendered as the first item in the dropdown, BEFORE
the data array's items but AFTER the implicit `<option value="">{placeholder}</option>`.

Typical uses:

```tsx
<DropDownList
  data={users}
  defaultItem={{ text: 'All users', value: '' }}
  textField="name"
  valueField="id"
  onValueChange={v => setFilter(v || null)}
/>
```

This produces three layers of "no-real-selection":

1. The native placeholder `<option value="">Select...</option>` —
   shown when `value` is `null`.
2. The `defaultItem` — visible as a real option the user can pick.
3. Each item in `data`.

Hosts who want **only** the defaultItem (no separate placeholder) must
ensure the defaultItem's `value` matches what they consider "empty"
(often `''`) and accept that the placeholder `<option value="">` ALSO
exists in the DOM. The visible distinction is invisible to most users
but observable through DevTools and screen readers.

`defaultItem` is NOT auto-selected. It is just an option the user can
pick. If you want it pre-selected, set `defaultValue` (uncontrolled) or
`value` (controlled) to its value.

---

## 6. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onValueChange` | `(v: string \| number \| null)` | User selects an option. Native `<select>`'s `onChange` fires; `''` is coerced to `null` so the placeholder selection is observable as "no value". Per the number-coercion caveat (§3.4), the payload is always a string (or `null`) regardless of the source item's value type. |

DropDownList does NOT expose:

- `onOpen` / `onClose` — the native `<select>` does not provide these
  events. The implementation cannot synthesize them without breaking
  the invisible-native pattern.
- `onFocus` / `onBlur` — not forwarded.
- `onKeyDown` — not forwarded; native keyboard nav (Up / Down / Home /
  End / typeahead) is handled by the browser.

For dropdowns that need open / close lifecycle hooks, use `ComboBox` or
`MultiSelect` (custom-popover components).

---

## 7. Slots

DropDownList has **no slot extensibility** in M1:
- No per-item custom rendering (items are plain `<option>` elements;
  HTML restricts their content to text).
- No header / footer / separator slots in the dropdown.
- No prefix / suffix on the trigger.
- No replacement of the chevron icon.

This is the trade-off for using a native `<select>`. Hosts who need
custom item rendering must use `ComboBox`.

---

## 8. Form integration

- **`name` attribute.** The underlying `<select>` participates in
  native form submission. Submitted value is the currently selected
  `value` (always a string in HTML — `''` for the placeholder; the
  data-item value otherwise).
- **`required` attribute.** Forwardable as a native attribute? **No**
  — DropDownList does NOT spread arbitrary `<select>` attributes (see
  §11 G-DDL2). Hosts who need HTML5 `required` semantics must own that
  validation in their form layer (React Hook Form, Zod, etc.) and
  surface errors via parent FormField wrapping.
- **Browser-native validation.** Same constraint as above — without
  `required` passthrough, the browser does not block submit on an
  empty selection.

---

## 9. Visual + interaction structure

```html
<div class="relative flex items-center [fillMode + rounded + size + className]">
  <select class="absolute inset-0 w-full opacity-0 cursor-pointer disabled:cursor-not-allowed">
    <option value="">{placeholder}</option>
    [defaultItem if present]
    [...data items, with disabled attribute honored]
  </select>
  <span class="pl-3 flex-1 truncate text-left pointer-events-none">
    {loading ? 'Loading...' : selectedItem?.text ?? <span class="text-muted-foreground">{placeholder}</span>}
  </span>
  <span class="pr-3 text-muted-foreground pointer-events-none shrink-0">
    {loading ? '⟳' : '▾'}
  </span>
</div>
```

Properties of this structure:

- The `<select>` covers the **entire** container (`inset-0`) so any
  click on the visible area opens the native picker.
- The visible `<span>` uses `truncate` for overflow — long item text
  becomes `"Very long item nam…"`. The native dropdown does NOT
  truncate.
- The `pointer-events-none` on both visible spans ensures clicks pass
  through to the underlying `<select>`.

### 9.1 Why the visible-text divergence is acceptable

Truncation in the trigger but not in the dropdown is intentional:
- The trigger has limited horizontal space.
- The dropdown can size itself to the natural item width (browser
  default).

The user reading the truncated text in the trigger can re-open the
dropdown to verify the full text — no information is lost.

---

## 10. Component composition

- **Filter / facet dropdowns.** `defaultItem={{ text: 'All', value: '' }}`
  + `onValueChange={v => setFilter(v || null)}`.
- **Form-field single-select.** Wrap in `FormField` for label + error.
- **Status / category pickers.** Where the option set is small and stable.
- **Country / language pickers.** Where option count is large (>50),
  consider `ComboBox` instead — DropDownList renders all items into
  the DOM at once.
- **Conditional question forms.** DropDownList's `onValueChange`
  drives conditional rendering of downstream fields.

---

## 11. Open questions (for council)

1. **`required` and other native passthrough (G-DDL2).** Should
   DropDownList spread arbitrary native `<select>` attributes
   (`required`, `aria-*`, `data-*`, `autoFocus`)?
2. **Item disabled rendering.** Disabled items render as native
   `<option disabled>`, which most browsers render in greyed-out text.
   No customisation hook in M1.
3. **Spinner animation (G-DDL3).** The loading `⟳` glyph is static.
   Should it be wrapped in `animate-spin` or replaced with a proper
   icon component?
4. **Auto-coerce numeric values (G-DDL4).** Should DropDownList detect
   when `data` items have numeric `value` fields and coerce
   `onValueChange` payloads back to numbers?
5. **Placeholder option visibility.** The implicit
   `<option value="">{placeholder}</option>` is always in the DOM,
   even when the user has selected a real value. Should we remove it
   after a real selection is made?

---

## 12. Deferred features

- **`multiple` selection** — use `MultiSelect`.
- **Free-text entry / autocomplete** — use `ComboBox`.
- **Custom item rendering** — use `ComboBox` or `DropDownTree`.
- **Item grouping with separators / headers** — use `ComboBox`.
- **Item icons** — use `ComboBox`.
- **Search-as-you-type filtering** — use `ComboBox`.
- **Virtualization for large lists** — use `ComboBox`.
- **`onOpen` / `onClose` lifecycle hooks** — native limitation.

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
| G-DDL1 | Critical | Native `<select>` always returns strings — numeric `value` items lose their type through `onValueChange` | [ACCEPTED-RISK 2026-06-06] §3.4 documents; hosts coerce in handler. Loose-equality in display layer masks the issue. |
| G-DDL2 | High | No native `<select>` attribute passthrough (`required`, `aria-*`, `data-*`, `autoFocus`) | [OPEN 2026-06-06] §11.1 council question — host-form-layer or native passthrough |
| G-DDL3 | Medium | Loading spinner glyph (`⟳`) is static — no rotation animation | [OPEN 2026-06-06] §11.3 — `animate-spin` or icon-component swap |
| G-DDL4 | Medium | No `onOpen` / `onClose` lifecycle hooks | [ACCEPTED-RISK 2026-06-06] §6 — native `<select>` limitation; hosts needing this use `ComboBox` |
| G-DDL5 | Medium | Item disabled rendering is browser-default; no customisation | [ACCEPTED-RISK 2026-06-06] §11.2 — fundamental `<option>` limitation |
| G-DDL6 | Medium | No per-item icons / custom rendering | [ACCEPTED-RISK 2026-06-06] §12 — use `ComboBox` for custom item rendering |
| G-DDL7 | Low | Normalization runs every render (no memoization) | [ACCEPTED-RISK 2026-06-06] §4 — measurable only for very large lists; use `ComboBox` |
| G-DDL8 | Low | `textField` / `valueField` silent fallback to `.text` / `.value` masks config bugs | [ACCEPTED-RISK 2026-06-06] §4 documents the fallback |
| G-DDL9 | Low | `defaultItem` coexists with the native placeholder `<option value="">` — two "no real selection" entries in the DOM | [ACCEPTED-RISK 2026-06-06] §5 documents; visible distinction is minimal |

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass; parallel with other dropdown expansions).
**Audit baseline:** DropDownList ~40% Kendo-minimum coverage. This section addresses all P1 audit misses.

> **Architecture note:** DropDownList's invisible-native-`<select>` pattern is structurally incompatible with
> client-side filtering and lifecycle events. P1 gaps that require a custom popover are documented below as
> architectural blockers requiring a future custom-popover variant (`DropDownListPro` or migration to `ComboBox`).
> Items that CAN be addressed within the native pattern are resolved inline. This is the canonical ruling on
> G-DDL2 / G-DDL4 scope.

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `DropDownListProps`:

```typescript
required?: boolean   // default: false — drives aria-required="true" on the native <select>
error?: boolean      // default: false — drives aria-invalid="true"; resolves G-DDL2 partial
```

**Semantics:**

| Prop | Native mapping | Visual mapping |
|---|---|---|
| `required` | `required` attribute on `<select>` + `aria-required="true"` | FormField asterisk via context (FormField reads `required` from `FormFieldContext` per FR-1 §4) |
| `error` | `aria-invalid="true"` on `<select>` | Error ring token on wrapper `<div>` |

`validationMessage` is NOT added — message composition is via `FormField` / `ValidationMessage` per FR-1 §3.

**Closes:** G-DDL2 (required + aria-invalid passthrough).

**wave-E1**

---

### §E1-2 FR-2 adoption — focus events (family ruling FR-2)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `DropDownListProps`:

```typescript
onFocus?: React.FocusEventHandler<HTMLSelectElement>
onBlur?: React.FocusEventHandler<HTMLSelectElement>
```

Both forward directly to the underlying `<select>` element.

`open` / `onOpenChange` are NOT adopted: DropDownList uses the native browser picker whose
open/close lifecycle is not scriptable. This is an accepted architectural limitation (G-DDL4,
ACCEPTED-RISK). Components needing popup lifecycle control must use `ComboBox` or `MultiSelect`.

**wave-E1**

---

### §E1-3 FR-3 size-vocabulary migration (family ruling FR-3)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-3.

Current `size` values (`'small' | 'medium' | 'large'`) migrate to `'sm' | 'md' | 'lg'`.
Deprecation aliases (`'small'` → `'sm'`, `'medium'` → `'md'`, `'large'` → `'lg'`) accepted
at runtime with a console.warn in development. Aliases removed at next major.

Same migration applies to `rounded` (currently `'small' | 'medium' | 'large' | 'full'` → `'sm' | 'md' | 'lg' | 'full'`).

**wave-E1**

---

### §E1-4 Loading state — formal contract (addresses G-DDL3)

`loading?: boolean` is already present. Formalize the contract:

1. When `loading={true}`: the `<select>` is `disabled`, the visible label shows `"Loading…"`, the
   chevron is replaced with a spinner. The spinner MUST use `animate-spin` (Tailwind) or the
   shared `<Spinner>` icon component — static `⟳` glyph is not acceptable at wave-E1.
2. `aria-busy="true"` is set on the wrapper `<div>` when `loading={true}`.
3. Popup suppression: the native `<select>` being `disabled` while loading already prevents popup
   open — no additional mechanism needed.

**Closes:** G-DDL3 (animated spinner).

**wave-E1**

---

### §E1-5 Architecture note — P1 gaps requiring custom popover (wave-E2+ or ComboBox migration)

The following P1 audit gaps are architecturally incompatible with the native-`<select>` pattern:

| Audit gap | Root cause | Resolution path |
|---|---|---|
| Client-side filtering (`filterable` prop) | Native `<option>` elements cannot be dynamically filtered; the browser controls popup rendering | Use `ComboBox` (catalog #32) — it is the filtering-capable sibling |
| Server-side filtering (`onFilterChange`) | Same — no scripting hook into native picker | Use `ComboBox` |
| `onOpen` / `onClose` lifecycle events (G-DDL4) | Native `<select>` does not fire scriptable open/close events | Use `ComboBox`; gap accepted (ACCEPTED-RISK 2026-06-06) |

These are accepted limitations of the native-select pattern, documented in §12 Deferred. No
`DropDownListPro` variant is planned for wave-E1; the ComboBox migration path is the canonical answer.

**wave-E2+ or WONT-FIX (native limitation)**

---

### §E1-6 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| Virtual scrolling | wave-E2+ (use ComboBox) |
| Item grouping (`groupField`) | wave-E2+ (requires popover; use ComboBox) |
| `itemRender` / `valueRender` custom slots | wave-E2+ (native limitation; use ComboBox) |
| `header` / `footer` popup slots | wave-E2+ (native limitation; use ComboBox) |
| `popupSettings` (width, animate) | wave-E2+ (native; use ComboBox) |
| Adaptive mode (responsive popup) | wave-E2+ |
