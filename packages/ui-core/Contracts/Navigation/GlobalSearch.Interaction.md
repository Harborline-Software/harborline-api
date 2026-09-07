# GlobalSearch — Interaction Contract

- **Component:** GlobalSearch
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GlobalSearch.Semantic.md) · [Interaction](./GlobalSearch.Interaction.md) · [Accessibility](./GlobalSearch.Accessibility.md) · [Styling](./GlobalSearch.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/GlobalSearch.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Input interactions

| Trigger | Effect |
|---|---|
| Type any character | Sets `query`, resets `activeIdx` to `-1`, opens dropdown, schedules `onSearch` after `debounceMs` |
| Clear input | Sets `results` to `[]`; dropdown shows `recentItems` if any |
| Focus input | Opens dropdown (shows recent items if query is empty and `recentItems` is non-empty) |

---

## 2. Keyboard navigation

| Key | Effect |
|---|---|
| `ArrowDown` | Increments `activeIdx` by 1, capped at `flat.length - 1`; prevents default scroll |
| `ArrowUp` | Decrements `activeIdx` by 1, floored at `0`; prevents default scroll |
| `Enter` | If `activeIdx >= 0`, calls `flat[activeIdx].onSelect()`, closes dropdown, clears query |
| `Escape` | Closes dropdown, blurs input |

ArrowDown/ArrowUp do not wrap (first item does not cycle to last and vice versa).

---

## 3. Mouse interactions

| Trigger | Effect |
|---|---|
| Click result item | Calls `item.onSelect()`, closes dropdown, clears query |
| `mouseenter` on result item | Sets `activeIdx` to that item's flat index |
| `mousedown` outside container | Closes dropdown |
| Click input while already open | No change (dropdown stays open) |

---

## 4. Debounce behaviour

`onSearch` is called via `setTimeout(fn, debounceMs)`. Each `query` change clears the prior timer. The timer is also cleared on unmount. Minimum debounce is governed by the `debounceMs` prop (default 150 ms). The host is responsible for memoising `onSearch` to avoid unnecessary re-render effects.

---

## 5. Result selection flow

1. User selects an item (click or Enter).
2. `item.onSelect()` is called — the host owns navigation.
3. Dropdown closes (`open = false`).
4. `query` is reset to `''`, `results` cleared.
5. Input loses focus only on Escape; selection does not explicitly blur the input.

---

## 6. Known gaps

| Gap | Description |
|---|---|
| No async `onSearch` | `onSearch` must return results synchronously. Async (Promise-based) search requires the host to manage results externally and pass them as `recentItems` or via a controlled variant. |
| No loading state | No spinner or skeleton while the debounce timer is pending. |
| No empty-state messaging | No "No results found" message when `results` is empty after a query. |
| ArrowDown does not wrap | Keyboard navigation clamps at the last item; no circular wrap. |
| No controlled query | `query` is always uncontrolled; host cannot seed an initial search string. |
