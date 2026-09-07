# ComboBox — Interaction Contract

- **Component:** ComboBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ComboBox.Semantic.md) · [Accessibility](./ComboBox.Accessibility.md) · [Styling](./ComboBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ComboBox.tsx`
- **Catalog row:** #32 ComboBox (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

- **Focus on input** → `setEditing(true)`, `setOpen(true)`, `setFilter('')`
- **Blur on input** → after 150ms: `setOpen(false)`, `setEditing(false)` (delay prevents blur-before-select race)
- **Toggle button click** → `setOpen(!open)`, `setEditing(true)`

---

## 2. Keyboard navigation

| Key | Effect |
|---|---|
| `ArrowDown` | Opens dropdown; advances `activeIdx` (clamps at `filtered.length - 1`) |
| `ArrowUp` | Advances `activeIdx` backward (clamps at 0) |
| `Enter` | Selects `filtered[activeIdx]` if `activeIdx >= 0` |
| `Escape` | Closes dropdown; `setEditing(false)` |

---

## 3. Option selection

`onMouseDown` (not `onClick`) fires on list items to prevent the input's `onBlur` closing the list before the click registers. Disabled items are silently ignored.

On selection: value state updates, `filter` resets to `''`, `editing=false`, `open=false`, `onValueChange(item.value)` fires.

---

## 4. Filtering

Typing in the input updates `filter` state and sets `open=true`. The filtered list shows items where `item.text.toLowerCase().includes(filter.toLowerCase())`. When `filterable=false`, all items always show.

---

## 5. Known gaps

Canonical gap table lives in [ComboBox.Semantic.md §7](./ComboBox.Semantic.md). Interaction-relevant excerpts:

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CBX1 | High | No `aria-controls` on input — listbox ID is not linked | **Blocking-before-v1-ship** — WCAG SC 4.1.2 Level A; see Semantic §7 for authoritative disposition |
| G-CBX2 | High | No `aria-activedescendant` on input — active option not announced to AT | **Blocking-before-v1-ship** — WCAG SC 4.1.2 Level A; see Semantic §7 for authoritative disposition |
| G-CBX3 | Medium | No `aria-autocomplete="list"` on input | Accepted-risk M1 |
| G-CBX4 | Low | `allowCustom` prop exists in interface but is not implemented — custom values are silently ignored | Accepted-risk M1 |
