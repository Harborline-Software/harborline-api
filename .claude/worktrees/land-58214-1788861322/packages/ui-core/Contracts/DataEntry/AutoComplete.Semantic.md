# AutoComplete — Semantic Contract

- **Component:** AutoComplete
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./AutoComplete.Interaction.md) · [Accessibility](./AutoComplete.Accessibility.md) · [Styling](./AutoComplete.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AutoComplete.tsx`
- **Catalog row:** #7 AutoComplete (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled listbox with native `<input>`

---

## 1. Component purpose

**AutoComplete** — a free-text input that shows suggestions from a data list. Unlike ComboBoxField, the user may type freely — the value is not constrained to the suggestions list.

---

## 2. Data model

```typescript
interface AutoCompleteItem {
  text: string
  [key: string]: unknown
}
```

`data` accepts `string[]` or `AutoCompleteItem[]`. When `AutoCompleteItem[]`, the `textField` prop (default: `'text'`) specifies which property holds the display text.

---

## 3. Props

```typescript
interface AutoCompleteProps {
  value?: string
  defaultValue?: string          // default: ''
  onChange?: (value: string) => void
  data: string[] | AutoCompleteItem[]
  textField?: string             // default: 'text'
  placeholder?: string
  disabled?: boolean
  loading?: boolean              // shows loading indicator
  minLength?: number             // default: 1; filter threshold
  delay?: number                 // default: 300ms; debounce before filtering
  size?: 'small' | 'medium' | 'large'
  fillMode?: 'solid' | 'outline' | 'flat'
  rounded?: 'small' | 'medium' | 'large' | 'full'
  id?: string
  name?: string
  className?: string
}
```

---

## 4. Value model

Controlled when `value` is provided; semi-controlled when omitted (component manages internal state, fires `onChange` with debounce).

Suggestions are capped at 10 items.

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** AutoComplete ~50% Kendo-minimum coverage. P1 misses: server-side filtering /
`onFilterChange`, `valid` / `required`, `aria-controls`, `aria-activedescendant` (G-AC2 / G-AC3).

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `AutoCompleteProps`:

```typescript
required?: boolean   // default: false — aria-required="true" on the <input>
error?: boolean      // default: false — aria-invalid="true" on input + error ring on wrapper
```

`validationMessage` is NOT added — FR-1 §3.

**wave-E1**

---

### §E1-2 FR-2 adoption — focus + popup events (family ruling FR-2)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `AutoCompleteProps`:

```typescript
onFocus?: React.FocusEventHandler<HTMLInputElement>
onBlur?: React.FocusEventHandler<HTMLInputElement>
open?: boolean
onOpenChange?: (open: boolean) => void
```

AutoComplete already has an open/close state machine (suggestion list shows/hides).
`open` / `onOpenChange` expose it as the FR-2 canonical pair. Uncontrolled default when `open`
is omitted.

**wave-E1**

---

### §E1-3 FR-3 size-vocabulary migration (family ruling FR-3)

`size` values migrate from `'small' | 'medium' | 'large'` → `'sm' | 'md' | 'lg'`.
`rounded` migrates similarly. Deprecation aliases at runtime; removed at next major.

**wave-E1**

---

### §E1-4 Loading state — formal contract (augments existing `loading` prop)

`loading?: boolean` is already present (covered/partial per audit — static glyph, no
`aria-live`). Formalize:

1. Spinner in trailing slot MUST animate (`animate-spin`).
2. `aria-busy="true"` on root wrapper when `loading={true}`.
3. **Popup suppression:** suggestion list MUST NOT open while loading (ArrowDown is no-op;
   focus opens list only when `loading={false}`).
4. An `aria-live="polite"` region announces loading completion: `"Suggestions available"` when
   `loading` transitions `true → false` and suggestions exist. This closes the partial audit row
   (existing implementation has no `aria-live`).

**wave-E1**

---

### §E1-5 Server-side filtering / `onFilterChange` (audit P1)

Add to `AutoCompleteProps`:

```typescript
onFilterChange?: (value: string) => void
```

When `onFilterChange` is provided:
- The `data` prop is treated as externally managed (server-filtered).
- Fires on every keystroke after `minLength` threshold is met, debounced by `delay` (300ms
  default — existing `delay` prop applies to both client and server paths).
- Host updates `data` and should set `loading={true}` during the fetch.

**wave-E1**

---

### §E1-6 ARIA combobox — mandatory rows (WCAG Level A; closes G-AC2/G-AC3)

**Required attributes (implementation obligations):**

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `aria-controls` | `<input>` | `"{id}-listbox"` (the suggestion list) | G-AC2 |
| `aria-activedescendant` | `<input>` | `"{id}-option-{index}"` of highlighted suggestion | G-AC3 |
| `role="listbox"` | Suggestion list container | literal | confirm present |
| `id="{id}-listbox"` | Suggestion list container | literal | matches aria-controls |
| `role="option"` | Each suggestion item | literal | confirm present |
| `id="{id}-option-{index}"` | Each suggestion item | literal | matches aria-activedescendant |
| `aria-selected="true/false"` | Each suggestion item | boolean | WAI-ARIA combobox 1.2 |
| `role="combobox"` | `<input>` | literal | confirm present |
| `aria-expanded` | `<input>` | `"true"` when list open | confirm present |
| `aria-autocomplete="list"` | `<input>` | literal | (G-CBX3 analog) |

`id` required; auto-generate via `useId()` if not provided.

**Keyboard (already present — confirm all implemented):**

| Key | Behavior |
|---|---|
| `ArrowDown` | Highlight first / next suggestion |
| `ArrowUp` | Highlight previous suggestion |
| `Enter` | Commit highlighted suggestion to input value |
| `Escape` | Close suggestion list; preserve current input value |
| `Tab` | Close suggestion list; no commit |

**wave-E1** — WCAG Level A, mandatory before v1-ship.

---

### §E1-7 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| `suggest` (inline completion / prefix fill) | wave-E2+ |
| `itemRender` / `valueRender` | wave-E2+ |
| `header` / `footer` popup slots | wave-E2+ |
| `popupSettings` | wave-E2+ |
| Adaptive mode | wave-E2+ |
| `clearButton` | wave-E2+ |
| Virtual scrolling | wave-E2+ |
