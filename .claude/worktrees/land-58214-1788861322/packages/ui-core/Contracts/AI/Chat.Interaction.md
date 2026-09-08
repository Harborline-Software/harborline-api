# Chat — Interaction Contract

- **Component:** Chat
- **ADR 0017 family:** AI
- **Contract type:** Interaction
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Chat.Semantic.md) · [Accessibility](./Chat.Accessibility.md) · [Styling](./Chat.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A26 Chat (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Chat / KendoReact AI Chat baseline)

---

## 1. Message submission

`Enter` submits (no Shift). `Shift+Enter` inserts newline. Send button click submits. Input is disabled when `disabled=true` or when the last assistant message has `status: 'pending' | 'streaming'`.

---

## 2. Auto-scroll

On new message arrival, the thread scrolls to the bottom. If the user has scrolled up (scroll position > 100px from bottom), auto-scroll pauses. A "scroll to bottom" button appears when auto-scroll is paused; clicking it re-enables auto-scroll.

---

## 3. Suggestions

Suggestion chips above the input: click fires `onSubmit(suggestion.prompt)` immediately. Chips hide after first user message.

---

## 4. Streaming cursor

Same blinking `|` pattern as AIPrompt during `status: 'streaming'`.

---

## 5. Error retry

Assistant messages with `status: 'error'` show a "Retry" icon button that re-fires `onSubmit` with the preceding user message's content (parent must wire this).

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CHAT1 | Medium | No message copy, edit, or delete actions | Deferred post-M1 |
| G-CHAT2 | Low | No conversation export | Deferred post-M1 |

---

## 7. Wave-N expansion — interaction additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred). Extends §1–§5 with interaction rules for all wave-N props in Semantic §8.

### 7.1 Global suggestion chips

Extends §3: clicking a global suggestion chip calls `onSuggestionClick?.(suggestion)` AND `onSubmit(suggestion.prompt)`. `onSuggestionClick` fires first. The parent may call `e.preventDefault()` on the chip's mouse event to suppress `onSubmit` if it wants to intercept before submission — however, because suggestion click is a button `onClick`, the standard approach is to conditionally pass `onSuggestionClick` without also wiring `onSubmit` when the parent drives the behavior entirely.

Chips hide after the first user message (`messages.length > 0 where role === 'user'` after submission). They reappear only if the parent clears `messages` back to empty.

### 7.2 Per-message suggested-action chips

Clicking a per-message action chip:
1. Fires `onActionExecute(action, message)`.
2. Does NOT auto-submit the message (the parent decides whether to post a new user message).
3. The chip does NOT disable after click — the parent should remove the `suggestedActions` from the message object if one-shot behavior is desired.

`quickActionsLayout` controls chip overflow: `'scroll'` clips to one row with horizontal scroll; `'wrap'` allows multi-row.

### 7.3 File attachment interactions

File bubble affordances:
- Clicking a file name or preview image fires `onFileAction` with command `'open'` (or the first item in `fileActions` if defined).
- Custom `fileActions` render as icon buttons on the file bubble. Each fires `onFileAction(action, file, message)`.
- `onDownload` fires for the download action and receives the full `files` array on that message (not just the single clicked file), matching Kendo semantics for bulk-download UI.

No drag-and-drop of attachments is specified at the Chat level — attachment input lives in the send-box (`PromptBox` attachments or custom `messageBox` slot).

### 7.4 Toolbar and context menu

**Hover/focus toolbar** (`messageToolbarActions`):
- The toolbar appears on pointer-hover or keyboard-focus-within of a message row.
- Tab enters the toolbar from the message itself. Toolbar buttons receive Tab focus in order. Escape returns focus to the message row.
- Clicking a toolbar button fires `onToolbarAction(action, message)`.

**Context menu** (`messageContextMenuActions`):
- Right-click or long-press (≥500ms) on a message bubble opens the context menu.
- The context menu is a standard `role="menu"` popup. Arrow keys navigate items; Enter/Space activates; Escape closes.
- Clicking an item fires `onContextMenuAction(action, message)` and closes the menu.

### 7.5 Load-more interactions

**`scrollMode: 'scrollable'` (default):**
- A "Load earlier messages" button appears above the first message when `total > messages.length`.
- Clicking fires `onLoadMoreMessages({ startIndex, pageSize })`. The button becomes disabled (showing a loading indicator) while the parent fetches. The parent re-enables it by setting `isLoading={false}` after prepending the new messages.

**`scrollMode: 'endless'`:**
- When the scroll position reaches the top of the thread (within `autoScrollThreshold` of the top), `onLoadMoreMessages` fires automatically.
- The component shows a top-of-list spinner while the parent has `isLoading={true}`.
- No manual button is shown.

### 7.6 Pin / unpin

- The unpin affordance (icon button) on a pinned-message header fires `onUnpin(message)`.
- The parent removes the `isPinned` flag; the component re-renders the message without the pin indicator.
- Pinned messages render a sticky header "Pinned message" above the bubble when a separate pinned-message panel is not available.

### 7.7 Reply reference click

- Clicking the quoted-message preview inside a reply bubble fires `onReferencedMessageClick(sourceMessage)`.
- The component does not scroll to the source message automatically — the parent handles navigation (e.g., `scrollIntoView` on the source message DOM node or virtualized scroll jump).

### 7.8 Typing indicator

When `typingUsers` is provided and non-empty, each entry renders its own three-dot bubble. Animation is staggered: the first entry animates at 0ms, each subsequent entry adds 200ms delay. When the array is cleared, the bubbles fade out (150ms opacity transition) before being removed from the DOM.

### 7.9 Send-box input control

When `inputValue` and `onInputValueChange` are provided:
- The send-box textarea is fully controlled.
- The parent must handle clearing (set `inputValue = ''`) after submission.
- The component does NOT clear the input internally in controlled mode.

When `speechToTextConfig` is enabled:
- Microphone button in the send-box; click starts recording.
- Transcribed text is appended (not replaced) to the current input value via `onInputValueChange`.
- Recording indicator (pulsing mic icon or `aria-label` change) shows active recording.
- Clicking the button again or pressing Escape stops recording.

### 7.10 Keyboard (additions to existing)

| Key | Context | Effect |
|---|---|---|
| `ArrowUp` / `ArrowDown` | Focus on message list | Navigate between `role="article"` message elements |
| `Tab` (on a message with toolbar) | Enters the toolbar | Focuses first toolbar button |
| `Escape` (focus in toolbar) | Returns focus to message | Hides the toolbar |
| `Enter` / `Space` (on toolbar button) | Toolbar button focused | Fires `onToolbarAction` |
| Context-menu key / `Shift+F10` | Focus on message | Opens context menu |
| `ArrowUp` / `ArrowDown` (in context menu) | Context menu open | Navigate menu items |
| `Escape` (context menu) | Context menu open | Closes menu, returns focus to message |
