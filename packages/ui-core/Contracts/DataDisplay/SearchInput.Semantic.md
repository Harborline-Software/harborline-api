# SearchInput — Semantic Contract

- **Component:** SearchInput
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SearchInput.Interaction.md) · [Accessibility](./SearchInput.Accessibility.md) · [Styling](./SearchInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SearchInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<input>` with search icon (no Radix)

---

## 1. Purpose

SearchInput is the canonical debounced text search input for list pages. It features:

- A leading search icon.
- A clear button (`✕`) when the input has content.
- Internal debouncing with configurable delay.
- External value synchronization (when the host applies a saved view or clears all filters, the input syncs).

SearchInput is a **controlled component** — the host owns the canonical `value`; SearchInput maintains a local copy for debounce purposes but always syncs from the external `value` when it changes.

**When to use SearchInput vs alternatives:**
- **SearchInput** (this component) — debounced text input for filtering a list
  already rendered on the page. No dropdown. Host owns results.
- **SearchField** — form-integrated `<input type="search">` with label/hint/
  error via FormFieldContext. Use inside a FormField when search is a form
  field (e.g., filter-panel save dialog). Fires `onSearch` on Enter.
- **GlobalSearch** — command-bar with live dropdown, category grouping,
  keyboard navigation, and recent-item fallback. Use for app-level navigation
  search (AppBar slot or Cmd+K overlay).

---

## 2. Data model

```typescript
interface SearchInputProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  debounceMs?: number
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string` | _required_ | The external canonical value. SearchInput syncs its local state when this changes. |
| `onChange` | `(value: string) => void` | _required_ | Called with the new value after the debounce delay. NOT called on every keystroke. |
| `placeholder` | `string` | `'Search…'` | Placeholder text for the input. Also used as the `aria-label` for the input. |
| `debounceMs` | `number` | `200` | Debounce delay in milliseconds. The `onChange` callback is deferred by this amount after the last keystroke. |
| `className` | `string` | `undefined` | Additional classes on the outer wrapper div. |

### 3.1 Local state vs external value

SearchInput maintains `localValue` as internal state for responsive typing. The external `value` is synced via:

```typescript
useEffect(() => {
  setLocalValue(value)
}, [value])
```

When `value` changes externally (e.g., saved view applied), `localValue` updates immediately. This may feel jarring if the user is mid-typing — but saved-view application is expected to be a deliberate action.

### 3.2 Debounce behaviour

On each keystroke:
1. `localValue` updates immediately (controlled input).
2. Existing debounce timer is cleared.
3. A new timer is set; `onChange(nextValue)` fires after `debounceMs`.

On clear (`handleClear`):
1. `localValue` set to `''`.
2. `onChange('')` fires **immediately** (no debounce on explicit clear).
3. Any pending timer is cleared.

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onChange` | `(value: string) => void` | After `debounceMs` ms following last keystroke, OR immediately on clear |

---

## 5. Slots

SearchInput has no slot props. The search icon and clear button are built-in.

---

## 6. Component composition

- **ListToolbar `search` slot.** SearchInput is the canonical content for the toolbar's search slot.
- **SavedViews integration.** When a saved view is applied, the host sets `value` to the saved search term; SearchInput syncs.
- **FilterBar pairing.** Hosts may display the active search as a FilterBar chip with `id="search"`.

---

## 7. Deferred features

- **Clear on Escape key** — pressing Escape to clear the search. Deferred; currently only the `✕` button clears.
- **Autocomplete suggestions** — a dropdown with search suggestions. Deferred; SearchInput is a plain text input.
- **Multi-field search** — searching across different fields. Deferred; host maps the single `value` to its query logic.
- **Highlighted match count** — "3 of 14 matches". Deferred; host computes.

---

## 8. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SI1 | Medium | No `aria-busy` spec during debounce window — AT users get no feedback while results are in-flight after typing | Fix-deferred M2 — add `aria-busy="true"` on the `<input>` for the debounce window duration (set on keystroke, clear when `onChange` fires); tracked as RA3-13 |
| G-SI2 | Medium | No live region spec for result counts — hosts that render "N results" text have no prescribed `aria-live` region; AT users miss result feedback | Fix-deferred M2 — prescribe `role="status"` or `aria-live="polite"` container managed by the host (outside SearchInput itself); tracked as RA3-13 |
