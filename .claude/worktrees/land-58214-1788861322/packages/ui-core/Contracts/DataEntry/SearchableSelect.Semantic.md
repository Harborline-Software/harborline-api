# SearchableSelect — Semantic Contract

- **Component:** SearchableSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SearchableSelect.Interaction.md) · [Accessibility](./SearchableSelect.Accessibility.md) · [Styling](./SearchableSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchableSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled filterable select (no Radix)

---

## 1. Purpose

SearchableSelect is a general-purpose searchable dropdown select. It renders a
trigger button with `role="combobox"`, an inline search input when open, a
grouped `role="listbox"` with `role="option"` items, and supports error display
and optional label. Options may have descriptions and be grouped. Search terms
are highlighted in matched option labels.

---

## 2. Data model

```typescript
interface SearchableSelectOption {
  value: string
  label: string
  description?: string
  disabled?: boolean
  group?: string
}

interface SearchableSelectProps {
  options: SearchableSelectOption[]
  value?: string
  onChange?: (value: string) => void
  placeholder?: string
  searchPlaceholder?: string
  disabled?: boolean
  label?: string
  id?: string
  required?: boolean
  error?: string
  emptyMessage?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `options` | `SearchableSelectOption[]` | _required_ | Full option list. Disabled options are excluded from the visible list. |
| `value` | `string` | — | Controlled selected value. |
| `onChange` | `(value: string) => void` | — | Called when an option is selected. |
| `placeholder` | `string` | `'Select an option'` | Trigger text when nothing selected. |
| `searchPlaceholder` | `string` | `'Search…'` | Placeholder in the search input. |
| `disabled` | `boolean` | `false` | Non-interactive trigger. |
| `label` | `string` | — | When provided, renders a `<label>` above the trigger. |
| `id` | `string` | — | Trigger id; auto-generated if not provided. |
| `required` | `boolean` | — | When `true`, adds `aria-required` and a red asterisk `*` to the label. |
| `error` | `string` | — | When provided, renders a `<p role="alert">` error below the trigger and applies error border to the trigger. |
| `emptyMessage` | `string` | `'No options found'` | Text shown when search yields no results. |
| `className` | `string` | — | Additional classes on root wrapper. |

### 3.1 Disabled option filtering

Disabled options (`disabled: true`) are excluded from the visible list. They
cannot be selected.

### 3.2 Option grouping

Options with a `group` string are grouped under a `role="presentation"` group
header `<li>`. Group order follows insertion order in the `options` array.

### 3.3 Search highlight

Matched text in option labels is wrapped in `<mark className="bg-yellow-100
text-inherit rounded-sm">`. Case-insensitive match on first occurrence.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `string` (option value) | User clicks an option in the dropdown. |

---

## 5. Internal state

| State | Default |
|---|---|
| `open` | `false` |
| `query` | `''` |

---

## 6. Composition

SearchableSelect is self-contained. The dropdown renders with `position:
absolute`; no portal. It integrates `useId()` for id generation. It does NOT
read from `FormFieldContext`.

---

## 7. Deferred features

- **Multiple selection.** Single-select only in M1.
- **Arrow-key navigation** in the dropdown.
- **Portal rendering** to escape overflow clipping.
- **Clearable selection** (no X button to deselect).

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** SearchableSelect ~45% Kendo-minimum coverage. P1 misses: server-side
filtering, `loading`, keyboard arrow navigation + `aria-activedescendant` (G1/G2/G3),
`aria-invalid` on trigger (G2).

> **Note:** SearchableSelect is not in the master catalog (implementation-first). The expansion
> below brings it to catalog-parity before it can be promoted to a v1 catalog entry.

---

### §E1-1 FR-2 adoption — focus + popup events (family ruling FR-2)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `SearchableSelectProps`:

```typescript
onFocus?: React.FocusEventHandler<HTMLButtonElement>
onBlur?: React.FocusEventHandler<HTMLButtonElement>
open?: boolean
onOpenChange?: (open: boolean) => void
```

`open` / `onOpenChange` expose the existing `open` internal state as the FR-2 canonical pair.

**wave-E1**

---

### §E1-2 FR-3 size + appearance axes (family ruling FR-3)

Add to `SearchableSelectProps`:

```typescript
size?: 'sm' | 'md' | 'lg'
fillMode?: 'solid' | 'outline' | 'flat'
rounded?: 'none' | 'sm' | 'md' | 'lg' | 'full'
```

Apply to the trigger button.

**wave-E1**

---

### §E1-3 Loading state with popup suppression (audit P1)

Add to `SearchableSelectProps`:

```typescript
loading?: boolean   // default: false
```

**Semantics:**

1. Animated spinner inside the trigger button (trailing area).
2. `aria-busy="true"` on the trigger button.
3. **Popup suppression:** dropdown MUST NOT open while `loading={true}`. Trigger click and
   keyboard activation are no-ops.
4. `aria-live="polite"` region announces `"Options loaded"` when `loading` transitions
   `true → false` and options are available.

**wave-E1**

---

### §E1-4 Server-side filtering / `onFilterChange` (audit P1)

Add to `SearchableSelectProps`:

```typescript
onFilterChange?: (value: string) => void
```

When `onFilterChange` is provided:
- Client-side filtering (existing substring match on `option.label`) is DISABLED.
- Fires on every keystroke in the search input; no debounce.
- Host updates `options` with filtered results and sets `loading={true}` during fetch.

**Pattern:**

```tsx
const [query, setQuery] = useState('')
const { data: options, isFetching } = useQuery(['options', query], () => search(query))

<SearchableSelect
  options={options ?? []}
  onFilterChange={setQuery}
  loading={isFetching}
  value={value}
  onChange={setValue}
/>
```

**wave-E1**

---

### §E1-5 Keyboard arrow navigation + ARIA (closes G1/G2/G3; WCAG Level A)

**These are P1 blockers: G1–G3 constitute keyboard inaccessibility + missing ARIA wiring
(WCAG 2.1.1 keyboard + 4.1.2 name/role/value Level A violations).**

**Required keyboard additions:**

| Key | Behavior |
|---|---|
| `ArrowDown` | When popup open: move highlight to next option |
| `ArrowUp` | When popup open: move highlight to previous option |
| `Enter` | Select highlighted option; close dropdown |
| `Escape` | Close dropdown; return focus to trigger |
| `Tab` | Close dropdown; move focus outside component |

When the popup opens (via trigger click or Enter/Space on trigger), focus moves to the search
input. ArrowDown from the search input moves highlight into the listbox without moving DOM focus
(keyboard navigation via `aria-activedescendant`, not DOM focus movement).

**ARIA additions (closes G2/G3):**

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `aria-activedescendant` | Trigger button `role="combobox"` | `"{id}-option-{index}"` of highlighted option | G3 |
| `id="{id}-option-{index}"` | Each `role="option"` item | literal | matches aria-activedescendant |
| `aria-invalid="true/false"` | Trigger button | from `error` prop (string present = invalid) | G2 |
| `aria-required` | Trigger button | from `required` prop | (already in M1 — confirm) |

Trigger ARIA review (confirm all present from M1):
- `role="combobox"` — confirm present
- `aria-expanded` — confirm present
- `aria-haspopup="listbox"` — confirm present
- `aria-controls="{id}-listbox"` — confirm present (G4 — `aria-controls` is documented as
  present in audit COVERED row; confirm matches listbox id)

**Closes:** G1 (arrow-key navigation in listbox), G2 (aria-invalid on trigger), G3 (aria-activedescendant).

**wave-E1** — WCAG Level A, mandatory before v1-ship.

---

### §E1-6 Label association fix (audit label partial)

Current: `<label>` uses `htmlFor` pointing to the trigger `<button>`. Non-standard; AT
announcements vary.

Preferred: when `label` prop is provided, render `<label id="{id}-label">{label}</label>` and
add `aria-labelledby="{id}-label"` to the trigger button. This is the correct pattern for a
button-based combobox (Radix-style).

**wave-E1**

---

### §E1-7 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| Portal rendering (escape overflow; G5) | wave-E2+ |
| `clearButton` / clearable (G4) | wave-E2+ |
| `allowCustom` / freeform entry | wave-E2+ |
| `itemRender` / custom option rows | wave-E2+ |
| `header` / `footer` popup slots | wave-E2+ |
| Virtual scrolling | wave-E2+ |
| Adaptive mode | wave-E2+ |
