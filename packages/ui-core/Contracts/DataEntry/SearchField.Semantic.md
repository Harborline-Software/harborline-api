# SearchField — Semantic Contract

- **Component:** SearchField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SearchField.Interaction.md) · [Accessibility](./SearchField.Accessibility.md) · [Styling](./SearchField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<input type="search">` with icon

---

## 1. Purpose

SearchField is a controlled `<input type="search">` with a decorative leading
search icon and a clear button that appears when the field has a value. It
integrates with `FormFieldContext` via `useFormField()` for id and
`aria-describedby`. It supports an `onSearch` callback fired on Enter, in
addition to the standard `onValueChange` for live filtering.

**When to use SearchField vs alternatives:**
- **SearchField** (this component) — FormField-integrated search input for
  search-as-form-field patterns. Use when search is part of a saved filter
  form, needs label/hint/error treatment, or needs to submit on Enter.
- **SearchInput** — standalone debounced list-filter. No form context, no
  label. Use when filtering a live list.
- **GlobalSearch** — command-bar with live dropdown and keyboard nav. Use for
  app-level navigation search.

---

## 2. Data model

SearchField is fully controlled. The host owns `value` and receives updates
via `onValueChange`.

```typescript
interface SearchFieldProps
  extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'onChange'> {
  value?: string
  onValueChange?: (value: string) => void
  onSearch?: (value: string) => void
  error?: boolean
}
```

SearchField extends `React.InputHTMLAttributes<HTMLInputElement>` (minus `type`
and `onChange`), so all standard HTML input attributes are passable as props.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string` | — | Controlled search value. |
| `onValueChange` | `(value: string) => void` | — | Called on every keystroke with the new string. |
| `onSearch` | `(value: string) => void` | — | Called when Enter is pressed. Receives the current `value`. Useful for server-side search where live filtering is undesirable. |
| `error` | `boolean` | — | When `true`, applies error styling and sets `aria-invalid`. |
| `...HTMLInputAttributes` | — | — | All other HTML input attributes are spread onto the native `<input>`. |

### 3.1 FormField integration

SearchField reads `id` and `describedBy` from `useFormField()`:

```typescript
const { id, describedBy } = useFormField()
```

- `id` is set on the input's `id` attribute for label linkage.
- `describedBy` is set as `aria-describedby`.

When used outside a FormField, these values are `undefined` and the
corresponding attributes are not set.

### 3.2 Clear button

The clear button renders when `value.length > 0` (controlled) or when the
component has a value. It calls `onValueChange('')` on click.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onValueChange` | `string` | Every keystroke. |
| `onSearch` | `string` | Enter key pressed. |

---

## 5. Composition

SearchField is composed inside a `FormField` for the standard label + hint +
error treatment. It uses `useFormField()` for context access.

---

## 6. Deferred features

- **Debounce option** (currently host-owned).
- **Loading indicator** while search is executing.
- **Search history / suggestions dropdown.**
