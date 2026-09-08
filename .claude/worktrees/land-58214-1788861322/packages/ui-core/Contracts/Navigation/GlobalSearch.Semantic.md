# GlobalSearch — Semantic Contract

- **Component:** GlobalSearch
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./GlobalSearch.Interaction.md) · [Accessibility](./GlobalSearch.Accessibility.md) · [Styling](./GlobalSearch.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/GlobalSearch.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<input>` with hand-rolled results

---

## 1. Component purpose

**GlobalSearch** — a command-bar–style search input with debounced results, category grouping, recent-item fallback, and keyboard-navigable dropdown. Intended as the application-level search widget placed in the AppBar or as a standalone overlay trigger.

**When to use GlobalSearch vs alternatives:**
- **GlobalSearch** (this component) — command-bar with live dropdown, category
  grouping, recent items, and keyboard-navigable results. Use for app-level
  navigation search placed in AppBar or triggered via keyboard shortcut.
- **SearchInput** — simple debounced list-filter input without dropdown. Use
  for filtering a list already on the page.
- **SearchField** — form-integrated search input for filter-panel or dialog
  contexts.

---

## 2. Data model

```typescript
interface SearchResult {
  id: string
  label: string
  sublabel?: string
  category?: string
  icon?: React.ReactNode
  onSelect: () => void
}
```

Results are flat items with an optional `category` string. Items sharing the same `category` are grouped visually under a heading. Items without a `category` form an anonymous group rendered without a heading.

---

## 3. Props

```typescript
interface GlobalSearchProps {
  placeholder?: string                              // default: 'Search…'
  onSearch: (query: string) => SearchResult[]
  recentItems?: SearchResult[]                     // default: []
  debounceMs?: number                              // default: 150
  className?: string
}
```

---

## 4. State model

The component manages four internal state values:

| State | Type | Description |
|---|---|---|
| `query` | `string` | Current input value |
| `results` | `SearchResult[]` | Latest set of results from `onSearch` |
| `open` | `boolean` | Whether the results dropdown is visible |
| `activeIdx` | `number` | Keyboard-focused result index (`-1` = none) |

### Display items

When `query.trim()` is non-empty, the dropdown displays `results`. When empty, the dropdown displays `recentItems`. This is the "recent items fallback" behaviour.

### Grouping

Items are grouped by `category` via `useMemo`. Category order follows insertion order. Items without a `category` form a group keyed by `''` rendered with no heading.

### Flat index

`flat` is a flattened ordered array of all display items used for keyboard navigation. The `activeIdx` is an index into `flat`.

---

## 5. Events

| Event | Trigger | Payload |
|---|---|---|
| `onSearch` | After `debounceMs` of idle typing | `query: string` |
| `SearchResult.onSelect` | User clicks a result item or presses Enter with it active | none (callback on the item object) |

`onSearch` is called synchronously after the debounce timer fires. It must return an array synchronously; async search is not built in.

---

## 6. Dropdown open/close rules

- Opens on `focus` of the input.
- Opens on `onChange` (any character typed).
- Closes on `Escape`.
- Closes when a result is selected (click or Enter).
- Closes on `mousedown` outside the container (`containerRef`).
- Only renders the `listbox` when `open && flat.length > 0`.

---

## 7. Composition

GlobalSearch is intended to be placed:
- In an AppBar, taking a fixed or flexible width.
- As a standalone component above a page body.
- Inside a modal or CommandPalette overlay (host owns the modal trigger).

GlobalSearch does not manage the modal/overlay layer itself.

---

## 8. Related components

- **CommandPalette** — a modal overlay search with richer actions; GlobalSearch is the always-visible inline form.
- **Menu** — provides click-triggered dropdown navigation; GlobalSearch is query-driven.
