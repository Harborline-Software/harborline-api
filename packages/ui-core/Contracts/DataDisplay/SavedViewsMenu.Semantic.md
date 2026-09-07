# SavedViewsMenu — Semantic Contract

- **Component:** SavedViewsMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SavedViewsMenu.Interaction.md) · [Accessibility](./SavedViewsMenu.Accessibility.md) · [Styling](./SavedViewsMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SavedViewsMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Radix UI `@radix-ui/react-popover`

---

## 1. Purpose

SavedViewsMenu provides UI for creating, applying, and deleting named saved views of a list page (filter state + sort state + column visibility state). It renders as a trigger button that opens a Radix Popover.

Key design decisions (from source comments):

- **Persistence is delegated.** The `SavedViewsStore` adapter handles read/write. v1 uses `localStorage`; swapping to a server-backed store requires only replacing the factory at the call-site.
- **Built on Radix Popover.** Click-outside and Escape are handled by Radix.
- **State partially internal.** The views list and the save-name input are internal state; filter/sort/column state is external.

---

## 2. Data model

```typescript
interface CurrentViewState {
  filters: SavedView['filters']
  sort: SavedView['sort']
  columns: SavedView['columns']
}

interface SavedViewsMenuProps {
  store: SavedViewsStore
  currentState: CurrentViewState
  onApply: (view: SavedView) => void
}
```

The `SavedViewsStore` interface (from `../../lib/savedViews`) provides:

```typescript
interface SavedViewsStore {
  list(): SavedView[]
  save(view: Omit<SavedView, 'id'>): void
  remove(id: string): void
}

interface SavedView {
  id: string
  name: string
  filters: unknown
  sort: unknown
  columns: unknown
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `store` | `SavedViewsStore` | _required_ | The persistence adapter. Component calls `store.list()`, `store.save()`, `store.remove()`. |
| `currentState` | `CurrentViewState` | _required_ | The current filter/sort/column state to persist when saving a new view. |
| `onApply` | `(view: SavedView) => void` | _required_ | Called when the user clicks a saved view to apply it. Host updates its filter/sort/column state. |

### 3.1 Internal state

| State | Type | Initial | Meaning |
|---|---|---|---|
| `views` | `SavedView[]` | `store.list()` | Current list of saved views. Updated on save, delete, and popover open. |
| `nameInput` | `string` | `''` | Text input value for the new view name. Cleared after save. |

### 3.2 Popover open refresh

On `onOpenChange(true)` (popover opens), `views` is refreshed via `store.list()`. This handles external mutations to the store between sessions.

### 3.3 Save semantics

When the user submits a name:

```typescript
store.save({ name: trimmed, ...currentState })
setViews(store.list())
setNameInput('')
```

The view is assigned an `id` by the store. The component re-reads the list.

### 3.4 Delete semantics

```typescript
e.stopPropagation()   // prevents the apply click from firing
store.remove(id)
setViews(store.list())
```

Delete fires while the popover stays open.

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onApply` | `(view: SavedView) => void` | User clicks a saved view name button to apply it |

---

## 5. Slots

SavedViewsMenu has no slot props.

---

## 6. Component composition

- **ListToolbar `actions` slot.** SavedViewsMenu is placed in the right actions cluster.
- **ColumnVisibilityMenu integration.** The `columns` field of `currentState` mirrors the `visibility` record from ColumnVisibilityMenu.
- **SortControl integration.** The `sort` field mirrors the `SortState` from SortControl.
- **FilterChips integration.** The `filters` field holds the active filter values from FilterChips/search state.

---

## 7. Deferred features

- **Rename view** — editing the name of an existing view. Deferred.
- **View import/export** — JSON export of the views list. Deferred.
- **Server-backed store** — W#80 plans to replace `localStorage` with an API-backed store. The `SavedViewsStore` interface is the abstraction boundary; only the factory changes.
- **View sharing** — sharing saved view URLs. Deferred.
- **Default view** — marking a view as "load on page open". Deferred.
