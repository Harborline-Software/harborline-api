# Chat — Accessibility Contract

- **Component:** Chat
- **ADR 0017 family:** AI
- **Contract type:** Accessibility
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Chat.Semantic.md) · [Interaction](./Chat.Interaction.md) · [Styling](./Chat.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A26 Chat (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Chat / KendoReact AI Chat baseline)

---

## 1. Message thread

Thread container: `role="log" aria-label="Conversation" aria-live="polite"`. Each message item: `role="article" aria-label="{user|assistant} message{, timestamp if present}"`. System messages: `role="status"`.

---

## 2. Input

Textarea: `aria-label="Message input"`. Send button: `aria-label="Send message"` with `aria-disabled` when disabled.

---

## 3. Streaming

`aria-live="polite"` on the thread container ensures AT announces new messages when complete. Streaming updates are NOT announced on every partial update — live region only re-announces when `status` transitions from `streaming` → `complete`. This matches the G-AIP-A1 mitigation pattern.

> **Implementation note:** Simply having `aria-live="polite"` on the thread container is insufficient to achieve "only announce on complete" behavior — React updates to `content` during streaming will trigger AT announcements on every re-render. Required DOM architecture: during `status: 'streaming'`, render the assistant message element with `aria-live="off"` (or outside the `role="log"` live region), and re-insert it into the live region only when `status` transitions to `complete`. Reference: WAI-ARIA ARIA19 "swap region" pattern.

---

## 4. Typing indicator

`role="status" aria-label="{Assistant name or 'AI'} is typing..."` for the three-dot animation container.

---

## 5. Scroll-to-bottom button

`aria-label="Scroll to latest message"` when visible.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CHAT-A1 | Medium | `role="log"` with many messages may be verbose for AT | Accepted-risk M1; virtual scrolling deferred; AT can navigate by article role |

---

## 7. Wave-N expansion — accessibility additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred). Extends §1–§5 with ARIA/keyboard specs for all wave-N interaction surfaces.

### 7.1 Toolbar actions

Per-message toolbar (`messageToolbarActions`):
- Toolbar container: `role="toolbar" aria-label="Message actions"`.
- Each button: `role="button" aria-label="{action.text}"`.
- Toolbar is hidden with `aria-hidden="true"` until the message is hovered or keyboard-focused. It is visible-and-focusable when `aria-hidden` is removed.
- Keyboard: Tab enters the toolbar from the message row; Escape dismisses and returns focus to the message.

### 7.2 Context menu

Context menu (`messageContextMenuActions`):
- Menu container: `role="menu" aria-label="Message options"`.
- Each item: `role="menuitem"`.
- The trigger (right-click zone or explicit trigger button) carries `aria-haspopup="menu"` and `aria-expanded`.
- When the menu opens, focus moves to the first `menuitem`.
- Arrow-key navigation; Escape closes and returns focus to the trigger.

### 7.3 File attachments

File attachment container: `role="group" aria-label="{n} file attachment{s}"`. Each file item: `role="listitem"` (within a `role="list"` wrapper). Download / action buttons on file items: `aria-label="{action.text}: {file.name}"`.

### 7.4 Suggested-action chips (per-message)

Per-message action chip row: `role="group" aria-label="Suggested actions"`. Each chip: `role="button" aria-label="{action.text}"`. The chip group inherits the message's `aria-label` via `aria-describedby` pointing to the message bubble's id.

### 7.5 Pin and reply

Pinned-message header: `role="note" aria-label="Pinned message"`. Unpin button: `aria-label="Unpin message"`.

Reply / quoted-message preview: `role="blockquote"` (or `role="note"` if blockquote is not supported in the AT target environment). The preview container: `aria-label="In reply to: {preview text}"`. Reference-click affordance: `role="button" aria-label="Jump to original message"`.

### 7.6 Load-more affordance

"Load earlier messages" button: `aria-label="Load earlier messages"`. While loading: `aria-disabled="true"` + `aria-busy="true"` on the button. When new messages prepend, AT should not re-read the full thread; the live region remains `aria-live="polite"` (new messages at the bottom still announce, but top-prepend inserts are outside the live region scope).

### 7.7 RTL support

When `dir="rtl"`, all directional ARIA attributes and landmark labels remain unchanged (they are language-neutral). Bubble alignment and layout token mirroring is a Styling concern.

### 7.8 Speech-to-text button

Microphone button (when `speechToTextConfig` enabled): `aria-label="Start speech to text"`. During active recording: `aria-label="Stop speech to text" aria-pressed="true"`. `aria-live="polite"` status region below the button announces `"Listening…"` when recording starts and `"Speech recognized"` when transcription completes.

### 7.9 Typing indicator (multi-user)

When `typingUsers` is provided, each typing bubble: `role="status" aria-label="{name} is typing…"`. When `typingUsers` is empty, the typing section is removed from the DOM entirely (not hidden with CSS) so AT does not announce a stale label.

### 7.10 Missing P1 gaps resolved by wave-N spec

| Gap ID | Severity | Description | Resolution |
|---|---|---|---|
| G-CHAT-A2 | P1 | No `dir` / RTL support | §8.9 Semantic (dir prop) + §7.7 above |
| G-CHAT-A3 | P1 | No `id` prop on root | §8.9 Semantic (id prop) |
| G-CHAT-A4 | P1 | No keyboard navigation between messages | Interaction §7.10 (ArrowUp/Down on message list) |
| G-CHAT-A5 | P1 | Context menu has no keyboard trigger | Interaction §7.10 (Context-menu key / Shift+F10) |
