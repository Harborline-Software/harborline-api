# PromptBox — Semantic Contract

- **Component:** PromptBox
- **ADR 0017 family:** AI
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PromptBox.Interaction.md) · [Accessibility](./PromptBox.Accessibility.md) · [Styling](./PromptBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/PromptBox.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled prompt text area with submit button

---

## 1. Component purpose

**PromptBox** — a textarea-based AI prompt input widget with an optional suggestions row and a submit button. The component manages only the input; it does not display AI output. Designed to be embedded within a larger AI surface (e.g. AIPrompt, Chat) or used as a standalone input for context-aware AI actions.

---

## 2. Props

```typescript
// Shared with AIPrompt, InlineAIPrompt, Chat suggestion families
interface AIPromptSuggestion {
  title: string
  subtitle?: string
  prompt: string
}

interface PromptBoxProps {
  value?: string                               // controlled mode
  defaultValue?: string                        // default: '' (uncontrolled seed)
  onChange?: (value: string) => void
  onSubmit?: (prompt: string) => void | Promise<void>
  loading?: boolean                            // default: false
  suggestions?: AIPromptSuggestion[]            // default: []; same shape as AIPrompt family: { title: string; subtitle?: string; prompt: string }
  placeholder?: string                         // default: 'Ask AI…'
  maxLength?: number
  showCharCount?: boolean                      // default: false
  disabled?: boolean                           // default: false
  className?: string
}
```

---

## 3. Controlled vs uncontrolled

PromptBox supports both modes:

- **Controlled:** `value` prop is provided. The component reads from `value` and calls `onChange(v)` on each change. The parent must update `value` to reflect changes.
- **Uncontrolled:** `value` is not provided. The component manages `internalValue` state seeded by `defaultValue`.

The active value is `controlledValue !== undefined ? controlledValue : internalValue`.

---

## 4. Suggestions

When `suggestions` is non-empty, a row of pill-shaped chip buttons renders above the textarea. Clicking a chip sets the textarea value to the chip's string (via `handleChange`). The parent's `onChange` fires. The submit action is NOT triggered automatically — unlike AIPrompt's suggestion-click behavior, PromptBox suggestions only populate the input.

---

## 5. Submit behavior

On submit (`handleSubmit`):
1. Guards: if `value.trim()` is empty or `loading` is `true`, submit is a no-op.
2. Calls `onSubmit?.(value.trim())` (may be async; awaited but no error handling built in).
3. The component does NOT clear the input after submit — clearing is the host's responsibility (via controlled `value` reset).

---

## 6. maxLength enforcement

If `maxLength` is provided and the new value's length exceeds `maxLength`, the change is silently dropped. No error state is shown. `showCharCount` (when `true` AND `maxLength` is set) displays `{value.length} / {maxLength}` in the footer bar.

---

## 7. Relationship to AIPrompt

| Dimension | PromptBox | AIPrompt |
|---|---|---|
| Output display | None | Inline output panel |
| Suggestion click | Populates input only | Populates and auto-submits |
| Controlled/uncontrolled | Both | Uncontrolled input |
| Loading label | "Thinking…" on submit button | N/A (no submit button in spec) |

PromptBox is the lower-level input primitive; AIPrompt composes an input + output display.

---

## 8. Wave-N expansion — Kendo PromptBox API parity (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred; spec complete). Follows DataGrid #1022 pattern.

### 8.1 File attachments

```typescript
interface PromptBoxAttachment {
  /** Unique identifier */
  uid: string
  name: string
  size?: number
  /** MIME type */
  type?: string
  /** Validation status */
  status?: 'ready' | 'uploading' | 'error'
  /** Validation message to display when status === 'error' */
  validationMessage?: string
}
```

- **`attachments?: PromptBoxAttachment[]`** — controlled attachment list. When provided, the parent manages the array (add/remove via callbacks). When omitted, no attachment UI renders.
- **`uploadButtonConfig?: boolean | { label?: string; icon?: React.ReactNode; accept?: string; multiple?: boolean }`** — configures the paperclip / attach-file button that appears at the start of the affix area. `false` hides the button even when `attachments` is provided. Default: `true` when `attachments` is wired.
- **`onAttachmentsChange?: (attachments: PromptBoxAttachment[]) => void`** — fires when the user adds or removes an attachment. The parent updates the `attachments` prop.

Attachment chips render above the textarea (in the `topAffix` zone) when `attachments` is non-empty. Each chip shows the file name + a remove (×) icon. Clicking × calls `onAttachmentsChange` with the chip removed.

### 8.2 Mode

- **`mode?: 'default' | 'single' | 'multi' | 'auto'`** — layout variant.
  - `'default'` (current behavior): multi-line textarea with `rows={3}`.
  - `'single'`: single-line input (`<input type="text">` replacing `<textarea>`). No `Shift+Enter` newline behavior.
  - `'multi'`: multi-line textarea, explicit `rows` controls height.
  - `'auto'`: auto-expanding — the textarea grows from 1 row up to the `maxTextAreaHeight` cap as the user types.

### 8.3 Submit button and action event

- **`actionButtonConfig?: boolean | { label?: string; icon?: React.ReactNode; className?: string }`** — configures the submit button. `false` hides it entirely. Default: `true` (shows the "Submit" / "Thinking…" button at existing spec §5).
- **`onPromptAction?: (event: { value: string; attachments?: PromptBoxAttachment[] }) => void`** — unified action event that fires on submit, carrying both the prompt value and current attachments. Recommended over `onSubmit` for wave-N callers; `onSubmit` is retained for backward compatibility.

### 8.4 Speech to text button

- **`speechToTextButtonConfig?: boolean | { language?: string; onError?: (e: unknown) => void }`** — microphone button in the affix area. `false` hides it. Default: `false`. Requires browser `SpeechRecognition` API; hidden when unavailable.
- When speech is active, transcribed text is appended to the current value via `onChange`.

### 8.5 Auto-grow and rows

- **`rows?: number`** — visible row count when `mode === 'multi'` or `mode === 'default'`. Default: `3` (current behavior). Has no effect in `mode === 'single'` or `mode === 'auto'`.
- **`maxTextAreaHeight?: string`** — max height (CSS string, e.g., `'200px'`) for auto-grow mode. When the content height exceeds this, a vertical scrollbar appears. Only applies in `mode === 'auto'`.

### 8.6 Affix slots

Slot areas for custom affordances. Slots render additional controls without replacing the textarea or the built-in buttons.

- **`startAffix?: React.ReactNode | ((props: { value: string }) => React.ReactNode)`** — renders at the start (left) of the input bar. Only visible in `mode === 'single'`. Example use: search icon, AI sparkle indicator.
- **`endAffix?: React.ReactNode | ((props: { value: string }) => React.ReactNode)`** — renders at the end (right) of the input bar, BEFORE the built-in action buttons. Example use: character picker, template selector.
- **`topAffix?: React.ReactNode | ((props: { attachments: PromptBoxAttachment[] }) => React.ReactNode)`** — renders above the textarea. Default use: attachment chips (when `attachments` is wired). Custom content can be provided to override.

When all three slots are empty and `attachments` is not provided, no affix areas render (existing layout preserved).

### 8.7 Appearance and form integration

Per FR-3 (family-wide ruling 2026-06-11):

- **`fillMode?: 'solid' | 'flat'`** — `'solid'` (default): bordered input with background. `'flat'`: no border, transparent background (for use inside already-bordered surfaces).
- **`readOnly?: boolean`** — renders the textarea as read-only. Input is not interactive; submit button is hidden. Default: `false`.
- **`inputAttributes?: React.InputHTMLAttributes<HTMLInputElement | HTMLTextAreaElement>`** — spread onto the inner focusable element. Use for `aria-label`, `aria-describedby`, `id`, `name`, `autoFocus`, etc.
- **`title?: string`** — sets the `title` attribute on the inner input element. Provides a tooltip; NOT the accessible name (use `inputAttributes.aria-label` for that).

Per FR-2 (family-wide ruling 2026-06-11):

- **`onFocus?: (event: React.FocusEvent) => void`** — fires when the inner input element receives focus.
- **`onBlur?: (event: React.FocusEvent) => void`** — fires when the inner input element loses focus.

`required` (FR-1) is NOT adopted — PromptBox is not a standard form input with a validation pipeline. The `inputAttributes` spread can carry `required` if the host needs it on the underlying element.

### 8.8 FR adoption rows

| Rule | Adoption |
|---|---|
| FR-1 (validation) | Not adopted at the prop level (`required` via `inputAttributes` only). PromptBox is an AI input, not a validated form field. |
| FR-2 (focus + popup) | `onFocus`/`onBlur` adopted (§8.7). No popup surface on PromptBox root. |
| FR-3 (appearance axes) | `fillMode` adopted (§8.7). `size` already present. `rounded`/`themeColor` deferred — PromptBox appearance is adequately controlled by `fillMode` + Styling tokens. |
