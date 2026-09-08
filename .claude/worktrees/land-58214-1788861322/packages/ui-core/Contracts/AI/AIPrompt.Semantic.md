# AIPrompt — Semantic Contract

- **Component:** AIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Semantic
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Interaction](./AIPrompt.Interaction.md) · [Accessibility](./AIPrompt.Accessibility.md) · [Styling](./AIPrompt.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A25 AIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik AIPrompt / KendoReact AI baseline)

---

## 1. Component purpose

**AIPrompt** — a single-turn AI input widget. Renders a prompt textarea with a send button and optional suggestion chips. The response is displayed inline below the input. Not a persistent conversation thread — for that, use Chat.

---

## 2. Props

```typescript
interface AIPromptSuggestion {
  title: string
  subtitle?: string
  prompt: string
}

interface AIPromptOutput {
  id: string
  prompt: string
  response: string | null
  status: 'pending' | 'streaming' | 'complete' | 'error'
}

interface AIPromptProps {
  onSubmit: (prompt: string) => void
  output?: AIPromptOutput | null
  suggestions?: AIPromptSuggestion[]
  placeholder?: string
  promptHeader?: string
  outputHeader?: string
  disabled?: boolean
  className?: string
}
```

---

## 3. Suggestions

When `suggestions` is provided and no `output` exists, suggestion chips are displayed below the input. Clicking a chip populates the textarea with `suggestion.prompt` and immediately fires `onSubmit`. The parent is responsible for calling the AI API and setting `output`.

---

## 4. Output states

- `null` — no output rendered.
- `status: 'pending'` — loading skeleton shown in the output panel.
- `status: 'streaming'` — text streams in progressively (parent pushes partial `response` string updates).
- `status: 'complete'` — full response rendered as Markdown.
- `status: 'error'` — error message shown in place of response.

---

## 5. Streaming

The component does not manage streaming internally. The parent calls the AI API, and updates `output.response` incrementally. The component re-renders on each update, appending new content.

---

## 6. Relationship to Chat

AIPrompt is stateless single-turn. Chat manages multi-turn conversation history. Use AIPrompt for contextual one-off queries; use Chat for conversational interfaces.

---

## 7. Wave-N expansion — Kendo AIPrompt API parity (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred; spec complete). Follows DataGrid #1022 pattern.

### 7.1 Controlled active view

The existing implementation manages `activeView` internally. Wave-N exposes the pair for external control:

```typescript
activeView?: 'prompt' | 'responses' | 'commands'
onActiveViewChange?: (view: 'prompt' | 'responses' | 'commands') => void
```

- When `activeView` is provided, the component operates in controlled tab mode. The parent must handle `onActiveViewChange` to update state.
- When `activeView` is omitted, the component manages its own tab state (existing behavior; unchanged).
- `views` (existing) remains the mechanism for restricting which tabs are available. `activeView` selects among the enabled views only.

### 7.2 Streaming token rendering

```typescript
streaming?: boolean   // default: false
```

- When `streaming={true}`, the component renders the last assistant message in the `responses` view with a blinking cursor appended to the partial content.
- The parent pushes incremental content via the controlled `messages`/`onMessageAdd` channel (existing §5); `streaming` is purely a visual-mode flag — it does not change the data contract.
- While `streaming` is `true`: the prompt input and send button are disabled (same as `loading`); a cancel affordance appears (see §7.3).
- `streaming` and `loading` are distinct: `loading` = waiting for first token (skeleton/spinner); `streaming` = tokens arriving (progressive text).

### 7.3 Cancel in-flight request

```typescript
onCancel?: () => void
```

- When `onCancel` is provided AND (`loading || streaming`), a Cancel button renders alongside the send button.
- Clicking Cancel fires `onCancel()`. The component does not reset its internal state; the parent is responsible for clearing `loading`/`streaming` and updating `messages` if the partial response should be kept or discarded.
- Cancel button label: `"Cancel"`. During `loading` state the send button is replaced by Cancel; during `streaming` state both are present (send = disabled; Cancel = active).

### 7.4 Command execution

```typescript
interface AIPromptCommand {
  /** Display text for the command in the commands view */
  text: string
  /** Optional icon to show beside the command text */
  icon?: React.ReactNode
  /** Opaque identifier passed to onCommandExecute */
  id: string
}

onCommandExecute?: (command: AIPromptCommand) => void
```

- The existing `suggestions` prop populates the `commands` tab (Semantic §3; no change).
- Wave-N renames the concept: `suggestions` in the `commands` view are treated as executable commands (not just prompt population).
- When `onCommandExecute` is provided, clicking a command in the commands view fires `onCommandExecute(command)` instead of populating the prompt textarea.
- When `onCommandExecute` is NOT provided, clicking a command falls back to the existing behavior (populates the textarea and switches to the `prompt` view).

### 7.5 Toolbar items

```typescript
toolbarItems?: React.ReactNode | AIPromptToolbarItem[]

interface AIPromptToolbarItem {
  /** Display label */
  text?: string
  icon?: React.ReactNode
  onClick?: () => void
  /** aria-label if text is omitted */
  ariaLabel?: string
}
```

- When provided, `toolbarItems` renders a row of icon/label buttons in the header area above the tab bar (or inline with the tab bar for `views.length === 1`).
- Common use: copy-response, regenerate, feedback thumbs, share.
- When `toolbarItems` is an array of `AIPromptToolbarItem` objects, each renders as a `<button>` with the given icon/text/click handler.
- When `toolbarItems` is a `ReactNode`, it renders as-is in the toolbar slot (fully custom).

### 7.6 Custom suggestions view

```typescript
suggestionsView?: React.ReactNode | React.ComponentType<{ suggestions: AIPromptSuggestion[]; onSelect: (s: AIPromptSuggestion) => void }>
```

- When provided, replaces the default grid/list rendering of the commands/suggestions panel in the `commands` view.
- If a component is passed, it receives `suggestions` and `onSelect`. The component is responsible for calling `onSelect` when the user picks a suggestion.

### 7.7 Layout and RTL

Per FR-3 (family-wide ruling 2026-06-11):

- **`dir?: string`** — text direction. `'rtl'` mirrors send button to left and tab alignment.
- **`style?: React.CSSProperties`** — inline styles on the root element.

AIPrompt does NOT adopt `size`/`fillMode`/`rounded`/`themeColor` — it is a multi-zone surface, not a single input. Per-zone appearance is controlled by the Styling contract tokens.

### 7.8 FR adoption rows

| Rule | Adoption |
|---|---|
| FR-1 (validation) | Not applicable — AIPrompt is not a form input field. |
| FR-2 (focus + popup) | No popup-bearing sub-component at the AIPrompt root. `onFocus`/`onBlur` on the prompt textarea are not exposed at this wave; the textarea already handles focus natively. |
| FR-3 (appearance axes) | `dir` and `style` adopted (§7.7). `size`/`fillMode`/`rounded`/`themeColor` deferred — AIPrompt is a multi-zone surface. |
