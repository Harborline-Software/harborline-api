# SearchableSelect — Interaction Contract

- **Component:** SearchableSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchableSelect.Semantic.md) · [Interaction](./SearchableSelect.Interaction.md) · [Accessibility](./SearchableSelect.Accessibility.md) · [Styling](./SearchableSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SearchableSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

- **Open:** click on the trigger `<button role="combobox">`.
- **Close:** mousedown outside the container, or selecting an option.
- **Escape key on trigger:** calls `setOpen(false)` (via `onKeyDown` on the
  trigger). This is the only keyboard close handler.

---

## 2. Search

When dropdown opens:
- `query` reset to `''`.
- Focus moved to search `<input>` after `setTimeout(0)`.

As the user types, `filtered` is recomputed. Disabled options are excluded
from filtered results regardless of query.

---

## 3. Option selection

`select(val: string)`:
1. Calls `onChange?.(val)`.
2. Calls `setOpen(false)`.

---

## 4. Keyboard behaviour

| Area | Key | Behaviour |
|---|---|---|
| Trigger | Enter / Space | Toggle open. |
| Trigger | Escape | Close dropdown. |
| Search input | Any character | Filter the list. |
| No list keyboard nav | — | Arrow keys in the dropdown are NOT handled. |

---

## 5. Chevron animation

Chevron rotates 180° when `open === true`, same as PropertySelector.

---

## 6. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | No arrow-key navigation in listbox | Keyboard-only users cannot navigate options after opening |
| G2 | No Enter-to-select on focused option | Keyboard users can only select by clicking with a mouse |
| G3 | Focus not returned to trigger after close | WCAG 2.4.3 Focus Order violation |
| G4 | No clearable selection — once selected, cannot deselect via the component | Host must handle deselection externally |
| G5 | Dropdown clips in overflow:hidden containers (no portal) | Popup may be cut off |
