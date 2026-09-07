# AutoComplete — Interaction Contract

- **Component:** AutoComplete
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AutoComplete.Semantic.md) · [Accessibility](./AutoComplete.Accessibility.md) · [Styling](./AutoComplete.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/AutoComplete.tsx`
- **Catalog row:** #7 AutoComplete (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Filtering

Filter triggers after `delay=300ms` debounce on input change. Shows suggestions when `inputValue.length >= minLength`. Case-insensitive substring match on item text. Max 10 results.

---

## 2. Open/close behavior

| Action | Effect |
|---|---|
| `onChange` (input change) | Open dropdown after debounce |
| `onFocus` | Open if suggestions exist |
| `onBlur` | Close after 150ms delay (prevents blur-before-mousedown race) |
| `Escape` | Close immediately |

---

## 3. Keyboard navigation

| Key | Condition | Effect |
|---|---|---|
| `ArrowDown` | Dropdown open | Move `activeIdx` to `min(i+1, len-1)` |
| `ArrowUp` | Dropdown open | Move `activeIdx` to `max(i-1, 0)` |
| `Enter` | `activeIdx >= 0` | `select(suggestions[activeIdx])` |
| `Escape` | Open | `setOpen(false)` |
| Other keys | Dropdown closed | No-op |

---

## 4. Selection

`select(text)`: sets internal/controlled value, fires `onChange`, closes dropdown.

`onMouseDown` on list items (not `onClick`) — prevents blur-before-select race condition.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AC2 | High | No `aria-controls` on input pointing to the listbox — AT cannot correlate the combobox with its listbox | Accepted-risk M1 |
| G-AC3 | High | No `aria-activedescendant` on input — AT won't announce the highlighted suggestion | Accepted-risk M1 |
| G-AC4 | Low | Loading indicator is a text `⟳` character with no ARIA announcement | Accepted-risk M1 |
