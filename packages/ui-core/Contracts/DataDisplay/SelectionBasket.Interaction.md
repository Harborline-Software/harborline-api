# SelectionBasket — Interaction Contract

- **Component:** SelectionBasket
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SelectionBasket.Semantic.md) · [Accessibility](./SelectionBasket.Accessibility.md) · [Styling](./SelectionBasket.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/SelectionBasket.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. State machine

Fully controlled — the basket itself has no internal item state.

```
Host sets items ─────────────> SelectionBasket re-renders
User presses ✕ on item ──────> onRemove(id) ──> Host updates items ──> re-render
User presses Clear ──────────> onClear()     ──> Host updates items ──> re-render
```

With the optional `useSelectionBasket` hook, the host half of each arrow is the hook's
`remove` / `clear`.

---

## 2. Control visibility (both rules are load-bearing)

| Condition | Result |
|---|---|
| `onRemove` omitted | **No** per-item remove controls render |
| `onClear` omitted | **No** clear control renders |
| `onClear` supplied AND `count === 0` | **No** clear control renders |
| `count === 0` | The empty state renders in place of the list |

Clear is hidden rather than disabled at count 0. A control that is present but does nothing is
a false affordance, and a disabled control gives the user something to puzzle over with no way
to resolve it.

---

## 3. Grouping behaviour

- `groupBy` absent ⇒ **one flat list**. Not "one group with no heading" — there is genuinely a
  single list element, so assistive tech is not told about a grouping that does not exist.
- `groupBy` present ⇒ one heading + one list per distinct key.
- **Group order = first-appearance order** of the key in `items`. Item order within a group is
  likewise first-appearance.
- `groupLabel` absent ⇒ the raw key is shown. Supply it whenever the key is an opaque id.

**Ordering is deliberately stable.** Groups are not sorted alphabetically or by size, because a
tray that reshuffles while the user is adding to it forces re-scanning after every action and
can move a control out from under the pointer.

---

## 4. Removal semantics

`onRemove` receives the item's `id`, never its index. Index-based removal breaks the moment
grouping reorders the visual sequence relative to the `items` array.

Removing the last item transitions the basket to its empty state, which also removes the clear
control (per §2).

---

## 5. Hook semantics

| Call | Present id | Absent id |
|---|---|---|
| `add(item)` | no-op (no duplicate) | appended at the end |
| `remove(id)` | removed | no-op |
| `toggle(item)` | removed | appended at the end |
| `clear()` | empties | no-op (already empty) |

`has(id)` is the predicate a checkbox binds its `checked` to; `toggle` is the matching change
handler. That pair is the intended checkbox-curation loop.

---

## 6. Not in scope

Drag-to-reorder, drag-to-add, selection ranges, keyboard multi-select (`Shift`/`Ctrl` semantics),
and persistence across sessions. The basket is a display + removal surface; acquiring items is
the host surface's job, and durable persistence belongs to the host's own state layer.
