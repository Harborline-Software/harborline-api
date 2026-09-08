# ListToolbar — Semantic Contract

- **Component:** ListToolbar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ListToolbar.Interaction.md) · [Accessibility](./ListToolbar.Accessibility.md) · [Styling](./ListToolbar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ListToolbar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled toolbar above list/grid

---

## 1. Purpose

ListToolbar is the canonical reactive toolbar for list and DataGrid pages. It provides a three-slot layout:

- **Left (search):** SearchInput, grows to fill available space.
- **Middle (filters):** Filter controls — Select dropdowns, date pickers, FilterChips.
- **Right (actions, ml-auto):** SortControl, ExportCsvButton, RefreshButton, ColumnVisibilityMenu, SavedViewsMenu.

Below the toolbar row, an optional "Clear filters" affordance appears when any filter or search is active.

ListToolbar is a **structural composition component** — it provides layout slots and wires the "clear filters" affordance. It holds no filter/search state itself.

---

## 2. Data model

```typescript
interface ListToolbarProps {
  search?: React.ReactNode
  filters?: React.ReactNode
  actions?: React.ReactNode
  hasActiveFilters?: boolean
  onClear?: () => void
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `search` | `ReactNode` | `undefined` | Left slot. Typically `<SearchInput />`. When provided, renders in a `flex-1 min-w-[12rem]` container. When absent, slot collapses. |
| `filters` | `ReactNode` | `undefined` | Middle slot. Filter controls. When provided, renders in `flex flex-wrap items-center gap-2`. When absent, slot collapses. |
| `actions` | `ReactNode` | `undefined` | Right slot. Renders with `ml-auto` to push to the right edge. When absent, slot collapses. |
| `hasActiveFilters` | `boolean` | `undefined` | When `true`, renders the "Clear filters" link below the toolbar row. ONLY active when BOTH `hasActiveFilters` and `onClear` are provided. |
| `onClear` | `() => void` | `undefined` | Called when the user clicks "Clear filters". Only rendered when this prop is provided AND `hasActiveFilters` is true. |

### 3.1 Self-containment constraint (Tailwind v4 note)

From the source comment: all Tailwind classes used in ListToolbar are literal strings. Dynamic class construction (e.g., `bg-${color}-100`) is prohibited — Tailwind v4 content detection only emits CSS for literal substrings. This constraint MUST be maintained if the component is extended.

### 3.2 Slot composition examples

| Use case | `search` | `filters` | `actions` |
|---|---|---|---|
| Full list page | `<SearchInput />` | `<FilterChips />`, `<StatusSelect />` | `<SortControl />`, `<ColumnVisibilityMenu />`, `<SavedViewsMenu />` |
| Read-only summary | `<SearchInput />` | — | `<ExportCsvButton />` |
| Filter-only view | — | `<FilterChips />` | — |

---

## 4. Events

| Event | Signature | When fired |
|---|---|---|
| `onClear` | `() => void` | User clicks "Clear filters" link |

---

## 5. Slots

| Slot prop | Rendered in | Notes |
|---|---|---|
| `search` | Left flex-1 container | Grows; `min-w-[12rem]` enforced |
| `filters` | Middle flex-wrap container | Gap-2 between items |
| `actions` | Right ml-auto container | Gap-2 between items |

All slots are optional; absent slots collapse (no empty placeholder rendered).

---

## 6. Component composition

ListToolbar is the outermost container for list page toolbar patterns. It composes:

- SearchInput (canonical left slot)
- FilterChips (filters slot)
- SortControl, ColumnVisibilityMenu, SavedViewsMenu, ExportCsvButton (actions slot)
- FilterBar (rendered BELOW ListToolbar by the host, not inside it)

---

## 7. Deferred features

- **Responsive collapse to icon-only** — mobile toolbar mode. Deferred; the `hidden sm:inline` pattern on child controls partially handles this.
- **Sticky toolbar** — toolbar that sticks to the top during scroll. Deferred; host applies `sticky top-0 z-10` via wrapper.
- **Toolbar skeleton** — loading state. Deferred.
