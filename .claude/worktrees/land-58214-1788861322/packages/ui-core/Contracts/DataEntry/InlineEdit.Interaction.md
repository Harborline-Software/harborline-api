# InlineEdit — Interaction Contract

- **Component:** InlineEdit
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./InlineEdit.Semantic.md) · [Interaction](./InlineEdit.Interaction.md) · [Accessibility](./InlineEdit.Accessibility.md) · [Styling](./InlineEdit.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/InlineEdit.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract covers the view/edit mode transition, commit, cancel, keyboard
shortcuts, and blur handling.

---

## 2. Mode transition: View → Edit

Triggers:
- Click on the view `<button>`.
- Keyboard: Tab to button + Enter or Space.

On entry to edit mode:
1. `editing = true`.
2. `draft` is set to current `value` (via `useEffect`).
3. Input `select()` is called to pre-select all text.
4. `autoFocus` moves focus to the input.

---

## 3. Edit mode — typing

The `<input>` is an uncontrolled draft (`draft` state). Every keystroke calls
`setDraft(e.target.value)`. No external `onChange` fires during typing.

---

## 4. Commit (edit → view)

Commit fires on:
- `blur` event on the input.
- `Enter` key (`e.preventDefault()` to suppress form submission).

`confirm()`:
1. Trims the draft.
2. If trimmed draft is non-empty AND differs from `value`, calls `onConfirm(trimmed)`.
3. Sets `editing = false`.

---

## 5. Cancel

Cancel fires on `Escape` key.

`cancel()`:
1. Resets `draft = value`.
2. Sets `editing = false`.

No `onConfirm` call.

---

## 6. Keyboard summary

| State | Key | Behaviour |
|---|---|---|
| View | Enter / Space | Enter edit mode. |
| View | Tab | Move focus to the next focusable element. |
| Edit | Enter | Commit and return to view mode. |
| Edit | Escape | Cancel and return to view mode. |
| Edit | Tab | Commit via blur + move focus out. |
| Edit | Any other key | Type into draft. |

---

## 7. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | Empty draft on blur/Enter does not call `onConfirm` and does not restore previous value — draft simply stays empty visually until the host provides a new `value` prop | If host does not notice, displayed value stays empty after clearing |
| G2 | No validation or error state during editing | Host cannot prevent confirm on invalid value |
