# AIPrompt — Interaction Contract

- **Component:** AIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./AIPrompt.Semantic.md) · [Accessibility](./AIPrompt.Accessibility.md) · [Styling](./AIPrompt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A25 AIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik AIPrompt / KendoReact AI baseline)

---

## 1. Prompt submission

`Enter` (without Shift) submits the prompt. `Shift+Enter` inserts a newline. Send button click submits. Both are disabled when the textarea is empty or `disabled=true` or when `output.status` is `pending` or `streaming`.

---

## 2. Suggestion chips

Clicking a chip fills the textarea and submits immediately. Chips disappear once `output` is non-null.

---

## 3. Clear / retry

When `output.status === 'complete'` or `'error'`, a "Clear" button resets the widget (parent responsibility to clear `output`). When `output.status === 'error'`, a "Retry" button re-fires `onSubmit` with the same prompt.

---

## 4. Streaming visual

During `status: 'streaming'`, a blinking cursor `|` appends to the streaming text.

---

## 5. Focus behavior

On mount, the textarea receives focus. After clearing, focus returns to the textarea.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AIP1 | Low | No copy-to-clipboard button on completed response | Resolved in wave-N via toolbarItems (§7.3) |

---

## 7. Wave-N expansion — interaction additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 7.1 Streaming state interactions

While `streaming={true}`:
- Send button is hidden/disabled (same behavior as `loading`).
- Cancel button (when `onCancel` is wired) is the only active submission-area control.
- Tab-switching away from `responses` is allowed during streaming — the stream continues in the background.
- On tab switch back to `responses`, the partially-streamed content is visible with the blinking cursor still active.

### 7.2 Cancel

Clicking the Cancel button fires `onCancel()`. The component immediately:
1. Removes the streaming cursor.
2. Marks the last assistant message display as static (no more partial updates expected).
3. Re-enables the prompt textarea and send button.

The component does NOT modify `messages` — the parent decides whether to append `"[Cancelled]"` or discard the partial response.

### 7.3 Toolbar items

Toolbar item buttons (from `toolbarItems` array):
- Each item is a `<button type="button">` with `onClick`.
- Focus cycles through toolbar items via Tab in DOM order.
- Items are disabled when `loading || streaming` by default unless the `AIPromptToolbarItem` explicitly specifies otherwise (future: `disabledWhileBusy?: boolean` field, wave-N+1).

### 7.4 Command execution

In the `commands` view:
- Clicking a command with `onCommandExecute` wired: fires `onCommandExecute(command)`. The active view does NOT automatically switch to `prompt` — the parent must call `setActiveView` (via the controlled `activeView`/`onActiveViewChange` pair) if a view transition is desired.
- Clicking a command without `onCommandExecute`: falls back to existing behavior (populates textarea, switches to `prompt` view).
- Keyboard: Enter or Space on a command item fires the same action as click.

### 7.5 Controlled view tab

When `activeView`/`onActiveViewChange` are wired:
- Tab button clicks fire `onActiveViewChange(view)` BEFORE the component updates visual state.
- The component transitions to the new view only when `activeView` prop updates to the new value.
- If `activeView` is provided but `onActiveViewChange` is not, the component is read-only on tab selection (tabs render but clicks have no effect — the parent must supply both props to enable controlled switching).

### 7.6 Keyboard (additions)

| Key | Context | Effect |
|---|---|---|
| `Escape` | Prompt textarea focused during `streaming` | Fires `onCancel()` if wired |
| `Enter` / `Space` | Command item in commands view | Fires `onCommandExecute` or populates prompt |
| `Tab` | Focus in toolbar area | Cycles through toolbar items; Tab-out leaves toolbar |
