# MultiSelect — Semantic Contract

- **Component:** MultiSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MultiSelect.Interaction.md) · [Accessibility](./MultiSelect.Accessibility.md) · [Styling](./MultiSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelect.tsx`
- **Catalog row:** #86 MultiSelect (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Radix UI `@radix-ui/react-popover` + hand-rolled multi-select list

---

## 1. Component purpose

**MultiSelect** — a multi-selection combobox displaying selected values as chips inside a trigger, with a searchable popover listbox for option selection. Integrates with FormField via `useFormField()`.

---

## 2. Props

```typescript
interface MultiSelectOption {
  value: string
  label: string
  disabled?: boolean
}

interface MultiSelectProps {
  name: string
  value: string[]
  onValueChange: (value: string[]) => void
  options: MultiSelectOption[]
  placeholder?: string      // default: 'Select…'
  disabled?: boolean        // default: false
  error?: boolean           // default: false
  emptyMessage?: string     // default: 'No options found.'
  maxDisplay?: number       // default: 3 — chips shown before "+N more" overflow
}
```

---

## 3. Always controlled

`value` and `onValueChange` are required. No uncontrolled mode.

---

## 4. Chip overflow

`displayChips = selectedLabels.slice(0, maxDisplay)`. When selected count exceeds `maxDisplay`, shows `"+N more"` text instead of additional chips. All selected values remain in `value[]` regardless.

---

## 5. Search filtering

A text input inside the trigger div allows filtering options. Filter is reset on open; case-insensitive substring match against `option.label`.

---

## 6. FormField integration

Consumes `useFormField()` to get `id` (applied to the trigger div) and `describedBy` (for `aria-describedby`).

> ⚠ **Native form submission:** MultiSelect does not participate in native HTML form submission in M1. The `name` prop is applied to the search `<input>` (which submits the current search text, not selected values). Hosts using standard form POST must extract selected values from the `value[]` prop manually. Do not use MultiSelect in a `<form>` with native submission without this workaround.

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MS3 | High | `name` prop wired to search `<input>`, not to a form-submission element — native form POST submits search text, not selected values | Fix-deferred M2 — add hidden `<input type="hidden"/>` per selected value; also tracked in MultiSelect.Interaction §6 |
| G-MS4 | Medium | `loading?: boolean` prop absent — no visual loading state for async-option-fetch; popover opens on click even when options are in-flight | Fix-deferred M2 — add `loading?: boolean`; render spinner in trigger; disable popover open when loading; set `aria-busy="true"` on trigger; tracked as RA-8 |

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** MultiSelect ~38% Kendo-minimum coverage. P1 misses: server-side filtering,
`loading` (G-MS4), `required`, keyboard arrow navigation (G-MS2 — WCAG 2.1.1 blocking),
`aria-activedescendant` (G-MS1), native form submission (G-MS3).

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `MultiSelectProps`:

```typescript
required?: boolean   // default: false
```

`error` is already present. `required` drives:
- `aria-required="true"` on the trigger `<div role="combobox">`.
- `FormFieldContext.required = true` so the FormField asterisk renders.

`validationMessage` is NOT added — FR-1 §3.

**wave-E1**

---

### §E1-2 FR-2 adoption — focus + popup events (family ruling FR-2)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `MultiSelectProps`:

```typescript
onFocus?: React.FocusEventHandler
onBlur?: React.FocusEventHandler
```

`open` / `onOpenChange`: MultiSelect already uses `Radix onOpenChange`. Expose the existing
Radix prop pair as the FR-2 canonical surface:

```typescript
open?: boolean
onOpenChange?: (open: boolean) => void
```

**wave-E1**

---

### §E1-3 FR-3 size axes — add to trigger (audit P2 promoted to wave-E1 given alignment)

Add to `MultiSelectProps`:

```typescript
size?: 'sm' | 'md' | 'lg'             // default: 'md'
fillMode?: 'solid' | 'outline' | 'flat'   // default: 'solid'
rounded?: 'none' | 'sm' | 'md' | 'lg' | 'full'  // default: 'md'
```

These axes apply to the trigger container (the div that shows chips + search input).
The Radix popover content dimensions are independent.

**wave-E1**

---

### §E1-4 Loading state with popup suppression (closes G-MS4; audit P1)

Add to `MultiSelectProps`:

```typescript
loading?: boolean   // default: false
```

**Semantics:**

1. Spinner in trigger trailing area (animated). Chips are still rendered so the current
   selection is visible while loading.
2. `aria-busy="true"` on trigger `<div>`.
3. **Popup suppression:** Radix `<Popover.Root>` MUST NOT open while `loading={true}`.
   Override `open={false}` on the Radix root when loading is `true`.
4. Search input is `disabled` while loading.

**Closes:** G-MS4.

**wave-E1**

---

### §E1-5 Server-side filtering / `onFilterChange` (audit P1)

Add to `MultiSelectProps`:

```typescript
onFilterChange?: (value: string) => void
```

When `onFilterChange` is provided:
- Client-side filtering (current substring match) is DISABLED.
- Fires on every keystroke in the search input; no internal debounce.
- Host updates `options` with filtered results and sets `loading={true}` during fetch.

**wave-E1**

---

### §E1-6 Native form submission — hidden inputs (closes G-MS3; audit P1)

Replace the current `<input name={name}>` (which submits the search text) with:

```html
<!-- one hidden input per selected value -->
<input type="hidden" name="{name}" value="{v}" />
```

When `value` is empty (`[]`), no hidden inputs are rendered (the field is absent from
the form submission payload — consistent with native `<select multiple>` behavior when
nothing is selected).

This change fixes the G-MS3 bug: native `<form>` POST now submits the selected value array
as repeated `name` entries, which server frameworks parse as a string array.

**Closes:** G-MS3.

**wave-E1**

---

### §E1-7 Keyboard arrow navigation in listbox (closes G-MS2; WCAG 2.1.1 — Level A)

**This is the most critical P1 gap — WCAG 2.1.1 violation (keyboard accessibility).**

**Required keyboard additions (implementation obligations):**

| Key | Context | Behavior |
|---|---|---|
| `ArrowDown` | Popup open, focus in search input | Move highlight to first option; then successive presses move down |
| `ArrowDown` | Popup open, focus in option list | Move highlight to next option (wraps to first) |
| `ArrowUp` | Popup open, focus in option list | Move highlight to previous option (wraps to last) |
| `Enter` / `Space` | Popup open, option highlighted | Toggle selection of highlighted option |
| `Backspace` | Focus in search input, search empty | Remove last chip from selection |
| `Escape` | Popup open | Close popup; return focus to trigger |
| `Tab` | Popup open | Close popup; move focus outside component |

**ARIA wiring required:**

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `aria-controls` | Trigger `<div role="combobox">` | `"{id}-listbox"` | WAI-ARIA combobox pattern |
| `aria-activedescendant` | Trigger `<div role="combobox">` | `"{id}-option-{index}"` of highlighted option | G-MS1 |
| `id="{id}-listbox"` | Listbox container | literal | matches aria-controls |
| `id="{id}-option-{index}"` | Each option `<div role="option">` | literal | matches aria-activedescendant |
| `aria-checked="true/false"` | Each option | boolean (multi-select uses checked, not selected) | WAI-ARIA listbox multi |
| `aria-multiselectable="true"` | Listbox container | literal | identifies multi-select mode to AT |

**Closes:** G-MS2 (keyboard navigation), G-MS1 (aria-activedescendant).

**wave-E1** — WCAG Level A, mandatory before v1-ship.

---

### §E1-8 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| Virtual scrolling | wave-E2+ |
| `groupField` / item grouping | wave-E2+ |
| `itemRender` / `tagRender` custom slots | wave-E2+ |
| `header` / `footer` popup slots | wave-E2+ |
| `popupSettings` | wave-E2+ |
| Adaptive mode | wave-E2+ |
| `allowCustom` (custom user values) | wave-E2+ |
| `autoClose` (keep open after select) | wave-E2+ |
