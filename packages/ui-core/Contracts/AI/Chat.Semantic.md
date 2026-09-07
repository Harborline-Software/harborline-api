# Chat — Semantic Contract

- **Component:** Chat
- **ADR 0017 family:** AI
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./Chat.Interaction.md) · [Accessibility](./Chat.Accessibility.md) · [Styling](./Chat.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A26 Chat (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Chat / KendoReact AI Chat baseline)

---

## 1. Component purpose

**Chat** — a multi-turn conversational UI showing a thread of user messages and AI responses. Supports streaming responses, message history, quick-reply suggestions, and custom message templates.

---

## 2. Data model

```typescript
type ChatMessageRole = 'user' | 'assistant' | 'system'

interface ChatMessage {
  id: string
  role: ChatMessageRole
  content: string
  status?: 'pending' | 'streaming' | 'complete' | 'error'
  timestamp?: Date
}

interface ChatSuggestion {
  title: string
  subtitle?: string
  prompt: string
}

interface ChatProps {
  messages: ChatMessage[]
  onSubmit: (prompt: string) => void
  suggestions?: ChatSuggestion[]
  placeholder?: string
  disabled?: boolean
  messageRender?: (message: ChatMessage) => React.ReactNode
  typingIndicator?: boolean
  className?: string
}
```

---

## 3. Message ownership

`role: 'user'` — right-aligned bubble. `role: 'assistant'` — left-aligned bubble. `role: 'system'` — centered muted label (status/context message, not directly authored by either party).

---

## 4. Streaming

The parent manages the message list. During streaming, the assistant message has `status: 'streaming'` and `content` is updated incrementally. The component renders progressively.

---

## 5. Typing indicator

When `typingIndicator=true` and the last message is a `pending` assistant message, show a three-dot animated typing indicator in place of the message bubble.

---

## 6. Suggestions

Quick-reply chips appear above the input when `suggestions` is provided. Clicking sends immediately. Suggestions disappear after the user sends any message.

---

## 7. History management

The component does not manage history — `messages` is the source of truth. Auto-scrolls to the bottom when new messages arrive unless the user has manually scrolled up (scroll-anchor detection).

---

## 8. Wave-N expansion — Kendo Chat API parity (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred; spec complete). Follows DataGrid #1022 pattern: all new props added in a single additive section; prior sections unchanged; implementation gated on wave scheduling.

### 8.1 Field-mapping model (controlled data with custom schemas)

Kendo Chat uses field-mapping props so callers can pass their own message objects without reshaping. Our contract adopts the same pattern as an alternative to the typed `ChatMessage` interface in §2. Both remain valid; field-mapping is additive for callers who already have server-shaped data.

```typescript
interface ChatFieldMapping {
  /** Field name for message text content. Default: 'text' */
  textField?: string
  /** Field name for the author identifier. Default: 'authorId' */
  authorIdField?: string
  /** Field name for the author display name. Default: 'authorName' */
  authorNameField?: string
  /** Field name for the author image URL. Default: 'authorImageUrl' */
  authorImageUrlField?: string
  /** Field name for the author image alt text. Default: 'authorImageAltText' */
  authorImageAltTextField?: string
  /** Field name for message timestamp. Default: 'timestamp' */
  timestampField?: string
  /** Field name for delivery status. Default: 'status' */
  statusField?: string
  /** Field name for message identifier. Default: 'id' */
  idField?: string
  /** Field name for file attachments. Default: 'files' */
  filesField?: string
  /** Field name for the deletion flag. Default: 'isDeleted' */
  isDeletedField?: string
  /** Field name for the failed-send flag. Default: 'isFailed' */
  isFailedField?: string
  /** Field name for the pinned flag. Default: 'isPinned' */
  isPinnedField?: string
  /** Field name for the replied-to message ID. Default: 'replyToId' */
  replyToIdField?: string
  /** Field name for suggested action chips attached to a message. Default: 'suggestedActions' */
  suggestedActionsField?: string
  /** Field name for attachment objects. Default: 'attachments' */
  attachmentsField?: string
}
```

When `fieldMapping` is omitted, the component falls back to the typed `ChatMessage` shape in §2.

### 8.2 Suggested actions

```typescript
interface ChatSuggestedAction {
  /** Display label on the chip */
  text: string
  /** Value passed to onActionExecute (defaults to text if omitted) */
  value?: string
  /** Optional icon rendered before the label */
  icon?: React.ReactNode
}
```

- **`suggestions?: ChatSuggestion[]`** — global suggestion chips rendered above the input (existing prop; no change to semantics).
- **Per-message `suggestedActions`** — individual messages may carry a `suggestedActions` array (via `suggestedActionsField`). Per-message chips render below the bubble. Clicking a chip fires `onActionExecute` with the action value.
- **`onSuggestionClick?: (suggestion: ChatSuggestion) => void`** — fires when a global suggestion chip is clicked. Complements the existing `onSubmit` pass-through. The parent decides whether to submit the suggestion text as a user message or treat it as a command.
- **`onActionExecute?: (action: ChatSuggestedAction, message: ChatMessage) => void`** — fires when a per-message suggested-action chip is clicked. The parent drives the response; the component does not auto-submit.
- **`quickActionsLayout?: 'scroll' | 'wrap'`** — controls overflow handling for per-message action chips. Default: `'scroll'` (single-row horizontal scroll). `'wrap'` allows multi-row.

### 8.3 File attachments

```typescript
interface ChatFile {
  /** Unique identifier for this file */
  id: string
  name: string
  size?: number
  /** MIME type */
  type?: string
  /** Preview URL (images) */
  url?: string
  /** Delivery/scan status */
  status?: 'uploading' | 'ready' | 'error'
}

interface FileAction {
  text: string
  icon?: React.ReactNode
  /** Opaque action identifier passed to onFileAction */
  command: string
}
```

- **`fileActions?: FileAction[]`** — per-file action buttons rendered on file bubbles (e.g., Download, Preview, Remove).
- **`onFileAction?: (action: FileAction, file: ChatFile, message: ChatMessage) => void`** — fires when a per-file action button is clicked.
- **`onDownload?: (files: ChatFile[], message: ChatMessage) => void`** — convenience shortcut for the download action (fires in addition to `onFileAction` when the download command is triggered).
- **`messageFilesLayout?: 'vertical' | 'wrap' | 'horizontal'`** — controls how file attachments are arranged within a message bubble. Default: `'vertical'`.

File attachments are read-only display in this surface (Chat renders files that exist on messages). Uploading new attachments is handled by the `PromptBox` `attachments` prop (§8.8) or by a custom `messageBox` slot (§8.6).

### 8.4 Message status, pin, and reply

**Delivery status:**
- `statusField` maps a status field on message objects (e.g., `'sent'`, `'delivered'`, `'read'`, `'failed'`).
- **`statusTemplate?: React.ComponentType<{ status: string; message: ChatMessage }>`** — custom renderer for the status indicator below each message bubble.
- Status display is author-side only (own messages); receiver messages show no delivery status.

**Pin:**
- **`pinnedMessages?: ChatMessage[]`** — out-of-batch pinned messages for remote mode (Kendo pattern; local-mode pinning tracks the flag on the message object itself via `isPinnedField`).
- **`onUnpin?: (message: ChatMessage) => void`** — fires when the user clicks the unpin affordance on a pinned message. The parent removes the pin flag.

**Reply / thread:**
- **`repliedToMessages?: ChatMessage[]`** — out-of-batch source messages for replies in remote mode. Local mode reads from the in-memory `messages` array by `replyToIdField`.
- **`onReferencedMessageClick?: (message: ChatMessage) => void`** — fires when the user clicks the quoted-message preview inside a reply bubble. Use to scroll to or highlight the original message.

### 8.5 Paging and load-earlier

```typescript
interface ChatLoadMoreEvent {
  /** Zero-based offset for the next page load */
  startIndex: number
  /** Requested page size */
  pageSize: number
}
```

- **`total?: number`** — total message count (server-known). Enables the load-more affordance when `total > messages.length`.
- **`pageSize?: number`** — batch size for paging requests. Default: `50`.
- **`startIndex?: number`** / **`endIndex?: number`** — remote-mode offset markers; the parent tracks these and passes updated values after each `onLoadMoreMessages` call.
- **`scrollMode?: 'scrollable' | 'endless'`** — `'scrollable'` (default): normal scroll; load-more button appears at the top when earlier messages exist. `'endless'`: scroll-triggered load (fires `onLoadMoreMessages` when user approaches the top boundary).
- **`autoScrollThreshold?: string | number`** — proximity to the bottom at which auto-scroll re-engages after manual scrolling. Default: `'20%'` of visible height. Accepts CSS length string (`'80px'`) or pixel number.
- **`scrollToBottomButton?: boolean`** — show/hide the floating scroll-to-bottom button. Default: `true`. Complementary to the Interaction §2 scroll-anchor behavior; set `false` to suppress the button UI while keeping the auto-scroll pause logic.
- **`onLoadMoreMessages?: (event: ChatLoadMoreEvent) => void`** — fires when the user requests earlier messages. The parent fetches, prepends to `messages`, and updates `startIndex`/`total`.

### 8.6 Message actions: toolbar and context menu

```typescript
interface MessageAction {
  /** Display label */
  text: string
  icon?: React.ReactNode
  /** Opaque command identifier */
  command: string
  /** Optional render condition per message */
  visible?: (message: ChatMessage) => boolean
}
```

- **`messageToolbarActions?: MessageAction[]`** — icons rendered in a hover/focus toolbar above a message bubble (copy, react, forward, etc.). Visible on hover or keyboard focus of the message.
- **`onToolbarAction?: (action: MessageAction, message: ChatMessage) => void`** — fires when a toolbar action button is clicked.
- **`messageContextMenuActions?: MessageAction[]`** — items in the right-click / long-press context menu on a message bubble.
- **`onContextMenuAction?: (action: MessageAction, message: ChatMessage) => void`** — fires when a context menu item is selected.

Deleted messages (`isDeletedField` truthy) render a tombstone ("This message was deleted") and suppress all toolbar/context-menu actions.

### 8.7 Typing indicators (extended)

The existing `typingIndicator?: boolean` (§5 / Semantic §5) triggers on `status: 'pending'` for the last assistant message. The wave-N expansion adds author-aware indicators for multi-participant scenarios:

- **`typingUsers?: Array<{ id: string | number; name?: string; avatarUrl?: string }>`** — explicit list of users currently typing. When provided, overrides the simple `typingIndicator` boolean. Each entry renders its own typing bubble.
- When `typingUsers` is empty, no typing indicator is shown regardless of `typingIndicator`.
- When `typingIndicator` is `true` and `typingUsers` is not provided, the existing single-bubble behavior is preserved (backward-compatible default).

### 8.8 Input area customization and speech

- **`messageBox?: React.ComponentType<{ onSend: (text: string) => void; disabled: boolean }>`** — replaces the built-in send-box entirely. When supplied, the component renders only the message thread and delegates all input to this slot.
- **`messageBoxSettings?: { placeholder?: string; maxLength?: number; rows?: number }`** — configuration passed to the built-in send-box (only applies when `messageBox` is not provided).
- **`sendButtonConfig?: boolean | { label?: string; icon?: React.ReactNode; className?: string }`** — customize or hide the send button. `false` hides the button entirely (Enter-only submission).
- **`speechToTextConfig?: boolean | { language?: string; onError?: (error: unknown) => void }`** — enables a microphone button in the send box that appends transcribed speech to the input. `false` (default) hides the button. Requires browser `SpeechRecognition` API; the component gracefully hides the button if the API is unavailable.
- **`inputValue?: string`** — controlled value for the send-box input. When provided, the parent also must handle `onInputValueChange` to keep the input in sync.
- **`onInputValueChange?: (value: string) => void`** — fires on every input keystroke when `inputValue` is controlled.

### 8.9 Appearance and layout

Per FR-3 (family-wide ruling 2026-06-11):

- **`dir?: string`** — RTL/LTR text direction. `'rtl'` mirrors the layout (message alignment, bubble corners, scroll controls).
- **`id?: string`** — DOM id for the root element.
- **`style?: React.CSSProperties`** — inline styles on the root element.
- **`height?: string | number`** — explicit container height. When set, the thread scrolls within this height. Default: fills parent (`h-full`).
- **`messageWidthMode?: 'standard' | 'wide' | 'full'`** — constrains bubble max-width. `'standard'` (default): `max-w-[80%]`. `'wide'`: `max-w-[92%]`. `'full'`: `max-w-full`.
- **`showUsername?: boolean`** — show/hide author name above each message bubble. Default: `true`.
- **`showAvatar?: boolean`** — show/hide author avatars. Default: `true`.
- **`timestampVisibility?: 'always' | 'onFocus' | 'never'`** — when timestamps render. Default: `'onFocus'` (visible on keyboard focus or hover of a message group).
- **`authorMessageSettings?: { className?: string }`** / **`receiverMessageSettings?: { className?: string }`** — per-side appearance overrides applied to all bubbles on that side.
- **`allowMessageCollapse?: boolean`** — when `true`, long messages can be collapsed to a truncated preview with a "Show more" affordance.

### 8.10 Render slots (templates)

All slots are optional; the component provides defaults for each.

| Prop | Type | Description |
|---|---|---|
| `messageTemplate` | `React.ComponentType<{ message: ChatMessage }>` | Full message row (avatar + bubble + actions). Replaces default layout entirely. |
| `messageContentTemplate` | `React.ComponentType<{ message: ChatMessage }>` | Bubble content only (inside the styled bubble div). Does not replace avatar or toolbar. |
| `attachmentTemplate` | `React.ComponentType<{ file: ChatFile }>` | Individual file attachment card inside a bubble. |
| `statusTemplate` | `React.ComponentType<{ status: string; message: ChatMessage }>` | Delivery status indicator below own messages. |
| `timestampTemplate` | `React.ComponentType<{ timestamp: Date }>` | Date-separator marker between message groups. |
| `headerTemplate` | `React.ReactNode \| (() => React.ReactNode)` | Static or dynamic chat header above the thread. |
| `noDataTemplate` | `React.ReactNode \| (() => React.ReactNode)` | Empty state when `messages` is empty. |
| `suggestionTemplate` | `React.ComponentType<{ suggestion: ChatSuggestion }>` | Custom chip renderer for global suggestion chips (§6). |

### 8.11 Resend failed messages

- **`onResendMessage?: (message: ChatMessage) => void`** — fires when the user clicks the Retry action on a message with `isFailed: true`. Complement to Interaction §5 (which currently fires `onSubmit` with the preceding user message). Wave-N updates §5: `onResendMessage` is preferred when available; `onSubmit` fallback is retained.

### 8.12 FR adoption rows

| Rule | Adoption |
|---|---|
| FR-1 (validation) | Not applicable — Chat is not a form input field. |
| FR-2 (focus + popup) | `scrollToBottomButton` affordance follows FR-2 focus semantics; no popup-bearing sub-component at the Chat root level. |
| FR-3 (appearance axes) | `dir`, `id`, `style`, `height` adopted (§8.9). `size`/`fillMode`/`rounded`/`themeColor` are NOT adopted — Chat is a full-surface component, not a form input; per-bubble appearance uses `authorMessageSettings`/`receiverMessageSettings` instead. |
