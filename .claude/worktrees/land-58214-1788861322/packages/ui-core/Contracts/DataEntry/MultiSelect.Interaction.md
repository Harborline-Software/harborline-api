# MultiSelect — Interaction Contract

- **Component:** MultiSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiSelect.Semantic.md) · [Accessibility](./MultiSelect.Accessibility.md) · [Styling](./MultiSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelect.tsx`
- **Catalog row:** #86 MultiSelect (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

- **Click on trigger div** (when not disabled) → `setOpen(true)` + focuses the search input (150ms timeout)
- **Popover onOpenChange** → `setOpen(o)` (Radix handles click-outside close)
- **Disabled** → open is suppressed

> **Testing note:** The 150ms focus timeout in the open handler (`setTimeout(() => searchRef.current?.focus(), 150)`) requires `vi.useFakeTimers()` / `jest.useFakeTimers()` in tests that assert search-input focus after trigger click. Advance timers by ≥150ms (`vi.advanceTimersByTime(200)`) before asserting focus.

---

## 2. Option toggle

Click on a listbox option → `toggle(v)`:
- If `value.includes(v)`: removes it (`value.filter(x => x !== v)`)
- Otherwise: appends it (`[...value, v]`)
- Calls `onValueChange(newArray)`
- Disabled options are silently ignored (no-op)

---

## 3. Chip removal

Click on `×` button inside a chip → `removeChip(v, e)`:
- `e.stopPropagation()` (prevents triggering the parent trigger click)
- `onValueChange(value.filter(x => x !== v))`

---

## 4. Backspace removal

When the search input is focused and empty (`!search`) and `value.length > 0`:
- `Backspace` key → removes the last selected chip: `onValueChange(value.slice(0, -1))`

---

## 5. Search filtering

Typing in the search input sets `search` state. Filtered options: `options.filter(o => o.label.toLowerCase().includes(search.toLowerCase()))`. No debounce.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MS1 | High | No `aria-activedescendant` on trigger — keyboard-highlighted option not announced to AT | Accepted-risk M1 |
| G-MS2 | High | No ArrowDown/Up keyboard navigation in the listbox — options are only clickable | Accepted-risk M1 |
| G-MS3 | Medium | `name` prop is applied to the search `<input>`, not to a hidden value input — form submission does not include selected values | Accepted-risk M1 |
| G-MS4 | Low | `onOpenAutoFocus` is prevented on the Popover content — AT focus is not moved to the listbox on open | Accepted-risk M1 |
