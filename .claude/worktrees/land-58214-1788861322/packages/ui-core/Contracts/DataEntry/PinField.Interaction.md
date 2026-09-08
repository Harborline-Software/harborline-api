# PinField — Interaction Contract

- **Component:** PinField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PinField.Semantic.md) · [Interaction](./PinField.Interaction.md) · [Accessibility](./PinField.Accessibility.md) · [Styling](./PinField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PinField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract describes PinField's multi-cell character entry, automatic focus
advance/retreat, arrow navigation, paste handling, and disabled/error states.

---

## 2. Character entry (single cell)

- **Trigger:** change event on a cell input.
- **Allowed characters:**
  - `'numeric'`: `[0-9]` only.
  - `'alphanumeric'`: `[0-9a-zA-Z]`.
- **Behaviour:**
  1. Take the last character of `e.target.value` (handles paste-to-single-cell
     and the native update where `value` goes from `""` to `"5"`).
  2. If the character fails the `allowed` regex, return (no change).
  3. Update `next[index] = char.toUpperCase()`.
  4. Call `onChange(next.join(''))`.
  5. If `char` is non-empty and `index < length - 1`: advance focus to cell
     `index + 1`.

---

## 3. Backspace

- **Trigger:** keydown with `e.key === 'Backspace'`.
- `e.preventDefault()` is called.
- **If the current cell has a character:** clear `chars[index]`, call
  `onChange(next.join(''))`. Focus stays on current cell.
- **If the current cell is empty and `index > 0`:** move focus to cell
  `index - 1`, clear that cell's character, call `onChange(next.join(''))`.

---

## 4. Arrow key navigation

- **ArrowLeft:** if `index > 0`, move focus to `index - 1`.
- **ArrowRight:** if `index < length - 1`, move focus to `index + 1`.
- No change to value.

---

## 5. Paste

- **Trigger:** paste event on any cell.
- `e.preventDefault()` is called.
- The pasted text is sliced to `length` characters.
- Allowed check: for `'numeric'` → `/^[0-9]+$/`; for `'alphanumeric'` →
  `/^[0-9a-zA-Z]+$/`. If the pasted string fails the check, the paste is
  discarded entirely.
- `onChange(pasted.toUpperCase().padEnd(length, '').slice(0, length))` —
  fills all cells with pasted content, padded with empty strings.
- Focus moves to `Math.min(pasted.length, length - 1)`.

---

## 6. Focus on cell click

`onFocus={(e) => e.target.select()}` — when a cell receives focus, its
content is selected. This allows the user to immediately overwrite the
character without needing to delete first.

---

## 7. Disabled state

- **Trigger:** `disabled === true`.
- **Behaviour:** all cell inputs receive native `disabled`.
- Visual: `cursor-not-allowed opacity-60`.

---

## 8. Error state

- **Trigger:** `error === true`.
- **Behaviour:** all cells receive `border-red-400 focus:ring-red-500`.
- Typing and focus remain enabled.

---

## 9. Keyboard behaviour summary

| Key | Behaviour |
|---|---|
| Allowed char key | Fill current cell; advance to next |
| Non-allowed char | Discard |
| Backspace | Clear current or previous cell; retreat focus |
| ArrowLeft | Move focus left |
| ArrowRight | Move focus right |
| Tab / Shift+Tab | Move focus to next/previous focusable (leaves PinField) |
| Ctrl+V / Cmd+V | Paste handled via `onPaste` (fills all cells) |

---

## 10. Known gaps

| # | Gap | Fix path |
|---|---|---|
| I-1 | Paste of partial string pads remaining cells with empty — onChange delivers padded value | Document as specified |
| I-2 | Tab moves focus OUT of PinField after the last cell — no Tab-within-field | Acceptable for current tab order; add `tabIndex` management if tab-within is needed |
