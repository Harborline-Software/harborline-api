# ListToolbar — Interaction Contract

- **Component:** ListToolbar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListToolbar.Semantic.md) · [Interaction](./ListToolbar.Interaction.md) · [Accessibility](./ListToolbar.Accessibility.md) · [Styling](./ListToolbar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ListToolbar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

ListToolbar is a **layout composition component**. Its own interaction surface is a single "Clear filters" button. All other interaction is delegated to its slot content.

---

## 2. State machine

ListToolbar has **no internal state**. It is stateless — the "Clear filters" button visibility is fully controlled by the `hasActiveFilters` prop.

```
hasActiveFilters=true + onClear provided ──> "Clear filters" button visible
hasActiveFilters=false (or onClear absent) ──> "Clear filters" button hidden
User clicks "Clear filters" ──────────────> onClear() ──> host updates state
```

---

## 3. "Clear filters" button behaviour

| Interaction | Result |
|---|---|
| Click "Clear filters" | `onClear()` fires; component does not optimistically hide the button — it disappears when the host sets `hasActiveFilters={false}` |
| Keyboard Enter/Space on "Clear filters" | Same as click |

---

## 4. Slot content interaction

ListToolbar makes no assumptions about slot content behaviour. Each slot component owns its own interaction:

- SearchInput: debounced input, clear button.
- FilterChips: toggle chips.
- SortControl: field select + direction toggle.
- ColumnVisibilityMenu: popover with checkboxes.
- SavedViewsMenu: popover with save/apply/delete.

---

## 5. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No toolbar-level loading/disabled state | Hosts must individually disable each slot's controls during loading |
| I2 | No "active filter count" summary | Host must aggregate from individual filter states |
