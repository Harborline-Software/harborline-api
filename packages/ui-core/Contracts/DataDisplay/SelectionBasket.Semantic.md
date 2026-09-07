# SelectionBasket — Semantic Contract

- **Component:** SelectionBasket
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SelectionBasket.Interaction.md) · [Accessibility](./SelectionBasket.Accessibility.md) · [Styling](./SelectionBasket.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/SelectionBasket.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1
- **Foundation:** none — hand-rolled tray over a plain list

---

## 1. Purpose

SelectionBasket renders a **persistent tray of items the user has curated out of one or more
other surfaces**: add/remove/clear, optional grouping by a caller-supplied key, and a count
badge.

### 1.1 Why it is not DataGrid selection or Transfer

The datagrid family already carries two neighbouring shapes; this is a third, and mixing them
up is the main risk this section exists to prevent.

| Shape | Scope | Grouping | Use when |
|---|---|---|---|
| `DataGrid` row selection + `bulkActions` | Ephemeral, scoped to one grid; lost on view change | none | "Act on these rows, now, in this grid" |
| `Transfer` | Owns the surface; two panels, available ↔ target | none | "Move items between two explicit lists" |
| **`SelectionBasket`** | **Persistent; accumulates ACROSS views** | **by a caller key** | **"Collect as I browse, act on the batch later"** |

### 1.2 Domain-free by construction

The component knows `id` and `label` and nothing more. The grouping axis, every visible label,
and every action are supplied by the caller. Domain vocabulary (what the items are, why they
were collected, what the batch is for) is **pack content** and must not appear here — the
platform-generic-copy rule applies to component defaults as much as to catalogs.

---

## 2. Data model

```typescript
interface SelectionBasketItem {
  id: string            // stable identity, unique within the basket
  label: string         // already-localized visible label
  description?: string  // optional secondary line
}

interface SelectionBasketProps<T extends SelectionBasketItem = SelectionBasketItem> {
  items: T[]
  groupBy?: (item: T) => string
  groupLabel?: (groupKey: string) => string
  onRemove?: (id: string) => void
  onClear?: () => void
  title?: string
  emptyLabel?: string
  className?: string
  'aria-label'?: string
}
```

The generic parameter `T` lets a caller pass richer items (carrying whatever `groupBy` reads)
without the basket knowing those extra fields exist.

---

## 3. Controlled, always

The component holds **no** item state. `items` is the single source of truth and the host owns
it. `onRemove` / `onClear` are notifications, not mutations — a host that ignores them renders
a basket that does not change.

---

## 4. The state contract (`useSelectionBasket`)

An **optional** headless companion for hosts that do not already own selection state:

```typescript
interface SelectionBasketApi<T extends SelectionBasketItem = SelectionBasketItem> {
  items: T[]
  count: number
  has: (id: string) => boolean
  add: (item: T) => void      // idempotent on a present id
  remove: (id: string) => void // no-op on an absent id
  toggle: (item: T) => void
  clear: () => void
}

function useSelectionBasket<T>(initialItems?: T[]): SelectionBasketApi<T>
```

Guarantees:

1. **Insertion order is preserved.** Items render in the order they were added.
2. **`add` is idempotent.** Adding a present id is a no-op, never a duplicate.
3. **Mutator identities are stable** across renders, including across state changes — safe to
   pass to memoized children.
4. **`initialItems` is read once, on mount.** Later changes to the argument are ignored, so a
   parent re-render cannot silently discard the user's curation.

---

## 5. Slots

None. Composition is via `groupBy` / `groupLabel` and the host's own surrounding layout; the
basket does not accept arbitrary children. Batch ACTIONS live outside the basket, in the host —
the basket shows what is collected, it does not decide what may be done with it.

---

## 6. Strings

| Key | Default | Note |
|---|---|---|
| `selectionBasket.title` | `Selection` | Overridable per instance via `title` |
| `selectionBasket.empty` | `Nothing selected yet` | Overridable via `emptyLabel` |
| `selectionBasket.countOne` / `.countOther` | `{count} selected` | CLDR plural pair |
| `common.clear` | `Clear` | reused |
| `feedback.removeItem` | `Remove {label}` | reused; interpolates the item label |

No user-facing string is hardcoded in English in the component.
