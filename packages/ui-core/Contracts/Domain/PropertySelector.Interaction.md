# PropertySelector — Interaction Contract

- **Component:** PropertySelector
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted (domain-relocated to Harborline App; NOT `@harborline-software/ui-react`)
- **Companion contracts:** [Semantic](./PropertySelector.Semantic.md) · [Interaction](./PropertySelector.Interaction.md) · [Accessibility](./PropertySelector.Accessibility.md) · [Styling](./PropertySelector.Styling.md)
- **Reference implementation:** the Harborline App's `src/property/PropertySelector.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

- **Open trigger:** click on the `<button role="combobox">`.
- **Close:** mousedown outside the container (document-level listener), or
  selecting an option.
- **No keyboard close** (Escape) in M1.

---

## 2. Search input

When the dropdown opens:
- `query` is reset to `''`.
- Focus is moved to the search `<input>` after a `setTimeout(0)` (one tick
  deferred to allow the DOM to render the input first).

As the user types, the property list is filtered in real time.

---

## 3. Option selection

Clicking a `<li role="option">`:
1. Calls `onChange?.(prop.id)`.
2. Sets `open = false`.

---

## 4. Keyboard behaviour

| Area | Key | Behaviour |
|---|---|---|
| Trigger button | Tab | Focus the trigger. |
| Trigger button | Enter / Space | Toggle open. |
| Search input | Any character | Filter the list. |
| No keyboard navigation in list | — | Arrow keys, Enter on option, Escape are NOT handled. |

This is a significant gap — see G1 below.

---

## 5. Chevron animation

The trigger button's chevron SVG rotates 180° when `open === true`:
`cn('w-4 h-4 text-gray-400 shrink-0 transition-transform', open && 'rotate-180')`.

---

## 6. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | No arrow-key navigation in the dropdown listbox | Keyboard-only users cannot navigate the list |
| G2 | No Escape key to close dropdown | Keyboard-only users cannot dismiss without selecting |
| G3 | Search focus uses `setTimeout(0)` — race-condition on slow renders | Focus may not land on search input |
| G4 | Dropdown clips inside overflow:hidden containers (no portal) | Popup may be invisible in certain layouts |
