# InlineAIPrompt — Semantic Contract

- **Component:** InlineAIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./InlineAIPrompt.Interaction.md) · [Accessibility](./InlineAIPrompt.Accessibility.md) · [Styling](./InlineAIPrompt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A27 InlineAIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik InlineAIPrompt baseline)

---

## 1. Component purpose

**InlineAIPrompt** — a compact AI query widget that sits inline with content (e.g., inside a text editor, next to a form field, or embedded in a data grid). Renders as a single-line input bar that expands to show the response. Shares the AIPrompt data model but optimized for narrow inline contexts.

---

## 2. Props

```typescript
interface InlineAIPromptProps {
  onSubmit: (prompt: string) => void
  output?: AIPromptOutput | null
  placeholder?: string
  triggerLabel?: string
  disabled?: boolean
  className?: string
}
```

`AIPromptOutput` — see [AIPrompt.Semantic.md §2](./AIPrompt.Semantic.md) for the full interface definition.

---

## 3. Trigger mode

When `output` is null and the component is not focused, it renders as a compact trigger button labeled `triggerLabel` (default: "Ask AI"). Clicking opens the inline input bar via expand animation.

---

## 4. Response display

Response appears below the input bar in a compact popover-style panel (not a full panel like AIPrompt). Renders as plain text (no full Markdown prose). `role="note"` container.

---

## 5. Relationship to AIPrompt

InlineAIPrompt is a compact variant. AIPrompt is the full-featured standalone widget. Use InlineAIPrompt when vertical space is constrained and the AI feature is secondary to surrounding content.

---

## 6. Wave-N expansion — Kendo InlineAIPrompt API parity (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred; spec complete). Follows DataGrid #1022 pattern.

### 6.1 Output and output card model

The existing contract specifies only the input surface. Wave-N adds the full output-display model — the primary purpose of InlineAIPrompt.

```typescript
interface InlineAIPromptOutput {
  /** Unique identifier for this output item */
  id: string
  /** The prompt text that generated this output */
  prompt: string
  /** AI response text (null while pending or streaming) */
  response: string | null
  /** Current state */
  status: 'pending' | 'streaming' | 'complete' | 'error'
  /** ISO timestamp of generation */
  timestamp?: string
}

interface InlineAIPromptOutputCard {
  /** Custom header content for the output card */
  header?: React.ReactNode | React.ComponentType<{ output: InlineAIPromptOutput }>
  /** Custom body content — replaces default response text rendering */
  body?: React.ReactNode | React.ComponentType<{ output: InlineAIPromptOutput }>
  /** Custom action buttons row — replaces default copy/discard buttons */
  actions?: React.ReactNode | React.ComponentType<{ output: InlineAIPromptOutput }>
}

interface OutputAction {
  /** Display label */
  text: string
  icon?: React.ReactNode
  /** Opaque command identifier passed to onOutputAction */
  command: string
}
```

Props:

- **`outputs?: InlineAIPromptOutput[]`** — collection of output cards to display. When non-empty, the output panel is visible below the input bar. Multiple outputs render as stacked cards (scroll within the output popup). The parent pushes incremental updates for streaming items.
- **`outputCard?: InlineAIPromptOutputCard`** — custom slot overrides for the card header, body, and actions area. When omitted, the component renders a default: prompt echo as header, response text as body, copy+discard as actions.
- **`outputActions?: OutputAction[]`** — replaces the default copy and discard action buttons on each output card with a custom set. When omitted, default copy + discard render.
- **`streaming?: boolean`** — active streaming state (same semantics as AIPrompt §7.2). When `true`, the last item in `outputs` is in streaming mode with the blinking cursor.

### 6.2 Output action callbacks

- **`onOutputAction?: (action: OutputAction, output: InlineAIPromptOutput) => void`** — fires when any custom output action button is clicked.
- **`onCopy?: (output: InlineAIPromptOutput) => void`** — fires when the default copy button is clicked. The component does NOT copy to clipboard automatically — the parent handles clipboard access (allows for rich-text copy vs plain-text copy decisions).
- **`onDiscard?: (output: InlineAIPromptOutput) => void`** — fires when the default discard button is clicked. The parent removes the output from `outputs`. The component does not mutate the `outputs` array.

### 6.3 Cancel

- **`onPromptCancel?: () => void`** — fires when the user cancels an in-flight request. Rendered as a cancel button (or stop icon replacing the generate button) while `streaming={true}` or `output?.status === 'pending'`.

### 6.4 Commands

```typescript
interface InlineAIPromptCommand {
  text: string
  icon?: React.ReactNode
  id: string
}
```

- **`commands?: InlineAIPromptCommand[]`** — list of commands rendered in a context menu accessible from the input bar (trigger: a `⌘` or commands icon button at the right of the input, or via a keyboard shortcut). Clicking a command fires `onCommandExecute`.
- **`onCommandExecute?: (command: InlineAIPromptCommand) => void`** — fires when a command is selected from the context menu.

### 6.5 Popup visibility control (FR-2 compliant)

Per FR-2 (family-wide ruling 2026-06-11), popup-bearing components adopt the canonical `open`/`onOpenChange` pair. The Kendo API uses `show`/`onOpen`/`onClose`; we map to FR-2:

- **`open?: boolean`** — controlled visibility of the inline popup (the expanded input + output panel). When omitted, the component manages its own open/closed state (existing behavior).
- **`onOpenChange?: (open: boolean) => void`** — fires when the component opens or closes. Replaces the Kendo `onOpen`/`onClose` pair with a single FR-2-canonical callback.
- **`anchor?: HTMLElement | null`** — the DOM element the popup positions relative to. When `null`, the component positions relative to the trigger button. Useful when InlineAIPrompt is mounted in a portal.
- **`appendTo?: HTMLElement`** — portal container for the popup. Default: the component's own DOM position (no portal). Use when InlineAIPrompt is inside an `overflow: hidden` ancestor.
- **`popupOptions?: object`** — additional positioning options (placement, offset) passed to the underlying popup primitive. Shape is intentionally opaque at this spec level; the implementation team defines the typed surface.
- **`animate?: boolean`** — enable/disable popup enter/exit animation. Default: `true`.

### 6.6 Input customization

- **`generateButton?: React.ComponentType<{ onClick: () => void; disabled: boolean }>`** — replaces the default generate/send button entirely. The custom component receives `onClick` (fires the prompt request) and `disabled`.
- **`promptInput?: React.ComponentType<{ value: string; onChange: (v: string) => void; placeholder?: string; disabled: boolean }>`** — replaces the default text input. The custom component must call `onChange` on input change events.

### 6.7 Sizing

- **`width?: string | number`** — explicit width of the expanded popup. Default: auto-size to content with `max-w-[360px]` cap (Styling contract §3).
- **`height?: string | number`** — explicit height of the output panel within the popup. Default: auto-size.

### 6.8 Speech to text

- **`enableSpeechToText?: boolean | { language?: string; onError?: (error: unknown) => void }`** — adds a microphone button to the right of the text input. `false` (default) hides it. Requires browser `SpeechRecognition` API; hidden gracefully when unavailable.

### 6.9 FR adoption rows

| Rule | Adoption |
|---|---|
| FR-1 (validation) | Not applicable — InlineAIPrompt is not a form input field. |
| FR-2 (focus + popup) | `open`/`onOpenChange` pair adopted (§6.5). `anchor`/`appendTo`/`popupOptions`/`animate` adopted. |
| FR-3 (appearance axes) | `size` already present in existing contract. `fillMode`/`rounded`/`themeColor` deferred — inline widget is scoped; appearance is Styling-contract territory. |
