# PromptBox — Interaction Contract

- **Component:** PromptBox
- **ADR 0017 family:** AI
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PromptBox.Semantic.md) · [Interaction](./PromptBox.Interaction.md) · [Accessibility](./PromptBox.Accessibility.md) · [Styling](./PromptBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/PromptBox.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Textarea interactions

| Trigger | Effect |
|---|---|
| Type in textarea | Calls `handleChange(v)` — updates internal/controlled value, calls `onChange?.(v)` |
| `Enter` (without Shift) | Calls `handleSubmit()` — submits if value non-empty and not loading |
| `Shift+Enter` | Inserts a newline (default textarea behaviour; not intercepted) |

---

## 2. Submit button interactions

| Trigger | Effect |
|---|---|
| Click submit button | Calls `handleSubmit()` |
| Submit button disabled | Button is disabled when `disabled || loading || !value.trim()`; clicks do nothing |

---

## 3. Suggestion chip interactions

| Trigger | Effect |
|---|---|
| Click a suggestion chip | Calls `handleChange(s)` — sets textarea to the chip's string |

Suggestion click does NOT automatically trigger submit.

---

## 4. Loading state

When `loading` is `true`:
- The textarea is disabled (`disabled || loading`).
- The submit button is disabled.
- The submit button label changes from `"Submit"` to `"Thinking…"`.

---

## 5. maxLength enforcement

`handleChange` guards: if `maxLength` is set and `v.length > maxLength`, the change is silently dropped. The existing value is preserved without any error message.

---

## 6. Submit guard

`handleSubmit` is a no-op when:
- `value.trim()` is `''` (empty or whitespace-only input).
- `loading` is `true`.

---

## 7. Known gaps

| Gap | Description |
|---|---|
| No auto-clear after submit | The input is not cleared after a successful submit. The host must reset `value` in controlled mode or the user must manually clear in uncontrolled mode. |
| No error handling on `onPromptRequest` | `handleSubmit` awaits `onPromptRequest?.(...)` but does not catch errors; unhandled promise rejections propagate. |
| Silent maxLength drop | When `maxLength` is exceeded, the change is silently dropped with no visual feedback or error announcement. |
| `onChange` called on suggestion click | Clicking a suggestion chip calls `onChange` (via `handleChange`) which may trigger side effects the host does not expect for a suggestion-fill action. |

---

## 8. Wave-N expansion — interaction additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 8.1 File attachment interactions

Attach-file button (when `uploadButtonConfig` is not `false`):
- Click opens the system file picker (standard `<input type="file">` trigger). `accept` and `multiple` are passed through from `uploadButtonConfig`.
- On file selection, `onAttachmentsChange` fires with the new files appended.
- Files are validated against `accept` and any `maxFileSize` at the PromptBox level — files that fail validation are included in the `onAttachmentsChange` payload with `status: 'error'` and `validationMessage` set; the parent may display or discard them.

Attachment chip remove (×):
- Click fires `onAttachmentsChange` with the removed item excluded.
- No confirmation dialog; removal is immediate.

### 8.2 Mode interactions

`mode === 'auto'` auto-grow:
- On each `onChange`, the textarea's scroll height is measured. If it exceeds `maxTextAreaHeight`, the textarea scrolls (no further grow). Otherwise the textarea height is set to `scrollHeight` (no explicit `rows`).
- Shrinks back on content deletion (height recomputed each `onChange`).

`mode === 'single'`:
- `Enter` submits immediately (same as `mode === 'default'`). `Shift+Enter` is a no-op (single-line input does not support newlines; the event is suppressed).

### 8.3 onPromptAction vs onSubmit

When both `onPromptAction` and `onSubmit` are wired:
- `handleSubmit` calls `onPromptAction({ value, attachments })` first, then `onSubmit(value)` second.
- This allows wave-N callers to use only `onPromptAction` and M1 callers to use only `onSubmit` without conflicts when both are present (e.g., a host that wraps both for migration).

When `onSubmit` is eventually deprecated (wave-N+1 or later), it will be removed; `onPromptAction` is the forward-path.

### 8.4 Speech to text

While recording:
- Input is disabled for typing (`disabled || recording`).
- Transcribed text is appended to the current value via a synthetic `onChange` call.
- Submit button remains enabled (the user can submit transcribed text while it accumulates).

Clicking the microphone again, or pressing Escape, stops recording.

### 8.5 Focus and blur events

`onFocus` fires when the inner textarea (or single-line input in `mode === 'single'`) receives focus — not when affix buttons receive focus.

`onBlur` fires when the inner textarea loses focus — not when focus moves to an affix button or suggestion chip (these are inside the component boundary, but focus remains within the component). Blur fires only on true focus-out (focus leaves the component boundary entirely).

### 8.6 readOnly mode

In `readOnly={true}`:
- Textarea / input is `readOnly`.
- Submit button is hidden (same as `actionButtonConfig={false}`).
- Suggestion chips are hidden.
- Attachment chips render (read display only — no remove buttons).
- Speech button is hidden.

### 8.7 Keyboard additions (wave-N)

| Key | Context | Effect |
|---|---|---|
| `Enter` (in single-line mode) | `mode === 'single'`, not loading | Submits |
| `Shift+Enter` (in single-line mode) | `mode === 'single'` | No-op (suppressed) |
| `Tab` | Attachment chip focused | Moves to next chip or next focusable element after the chip list |
| `Delete` / `Backspace` | Attachment chip focused | Fires `onAttachmentsChange` removing that chip |
