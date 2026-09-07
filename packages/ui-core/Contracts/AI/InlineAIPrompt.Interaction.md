# InlineAIPrompt — Interaction Contract

- **Component:** InlineAIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./InlineAIPrompt.Semantic.md) · [Accessibility](./InlineAIPrompt.Accessibility.md) · [Styling](./InlineAIPrompt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A27 InlineAIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik InlineAIPrompt baseline)

---

## 1. Open / close

Click the trigger button → input bar expands (width + fade-in, 150ms). `Escape` collapses without submitting. Clicking outside the component collapses if no output is showing.

---

## 2. Submission

`Enter` submits. Input is disabled during `pending` / `streaming`. Send icon button at the right of the input bar.

---

## 3. Response dismissal

When `output.status === 'complete'` or `output.status === 'error'`, a close (×) button on the response panel allows dismissal. Parent should clear `output` on dismiss. Both states use the same dismiss callback; the caller determines whether to retry on error.

---

## 4. Focus

On open, input receives focus. On close (Escape / dismiss), focus returns to the trigger button.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-INLAI1 | Low | No retry on error — user must close and reopen | Resolved in wave-N: discard + re-prompt is the intended flow; onPromptCancel provides cancel path |

---

## 6. Wave-N expansion — interaction additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 6.1 Output card interactions

For each output card in `outputs`:
- **Copy button** (default or custom): fires `onCopy(output)`. The button label changes to a checkmark for 1.5s ("Copied") then reverts — this visual confirmation is internal to the component; the parent receives the callback regardless.
- **Discard button** (default or custom): fires `onDiscard(output)`. The card fades out (150ms) then the component fires `onDiscard`. The parent removes the output from `outputs` to fully clear it.
- **Custom output actions** (`outputActions`): each button fires `onOutputAction(action, output)`.

When `outputs` becomes empty (all discarded or cleared by parent), the output panel collapses and the component returns to its compact trigger state.

### 6.2 Commands context menu

Commands menu trigger (when `commands` is non-empty):
- A `⌘` icon button at the right of the input bar (before the generate button) opens the context menu.
- Keyboard: `/` key while the input is focused and empty also opens the commands menu (progressive enhancement; no conflict when the input has content).
- Menu closes on item selection, on Escape, and on click-outside.
- Selecting a command fires `onCommandExecute(command)`.

### 6.3 Cancel

While `streaming={true}` or `output?.status === 'pending'`:
- The generate button is replaced by a stop/cancel button.
- Clicking fires `onPromptCancel()`.
- Pressing Escape while the popup is open also fires `onPromptCancel()` if in-flight. If not in-flight, Escape closes the popup (existing §1 behavior).
- **Escape priority:** in-flight cancel takes priority over popup-close. Both actions are mutually exclusive.

### 6.4 Multi-output navigation

When `outputs.length > 1`:
- Output cards stack vertically within the popup and scroll.
- Keyboard: `ArrowUp` / `ArrowDown` navigate between output cards when focus is inside the output panel.
- Each card's action buttons are reachable via Tab.

### 6.5 Controlled open/close (FR-2)

When `open`/`onOpenChange` are provided:
- The trigger button click fires `onOpenChange(true)` rather than directly opening.
- The component opens only when `open` prop becomes `true`.
- Click-outside and Escape fire `onOpenChange(false)`.
- The component DOES NOT close itself; the parent drives all state transitions.

### 6.6 Custom input and generate button

`promptInput` custom component:
- Receives `value`, `onChange`, `placeholder`, `disabled`.
- Enter keydown within the custom input triggers prompt submission (the component wraps the custom input and intercepts `onKeyDown` for Enter, same as the default).

`generateButton` custom component:
- Replaces the icon button at the right of the input bar.
- The component passes `disabled` when input is empty or in-flight.

### 6.7 Keyboard additions

| Key | Context | Effect |
|---|---|---|
| `/` | Input focused + empty | Opens commands context menu |
| `ArrowUp` / `ArrowDown` | Focus inside output panel | Navigate between output cards |
| `Escape` | In-flight request active | Fires `onPromptCancel()` (priority over popup-close) |
| `Escape` | Popup open, not in-flight | Closes popup (existing §1) |
| `Tab` | Last output action button | Moves focus to next focusable element outside the popup |
