# PromptBox — Accessibility Contract

- **Component:** PromptBox
- **ADR 0017 family:** AI
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PromptBox.Semantic.md) · [Interaction](./PromptBox.Interaction.md) · [Accessibility](./PromptBox.Accessibility.md) · [Styling](./PromptBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/PromptBox.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Element | Role / Attribute | Value |
|---|---|---|
| `<textarea>` | `placeholder` | From `placeholder` prop (default `"Ask AI…"`) |
| `<textarea>` | `disabled` | Set when `disabled || loading` |
| Submit `<button>` | `disabled` | Set when `disabled || loading || !value.trim()` |
| Suggestion chips `<button>` | `type` | `"button"` |

No explicit `aria-label` or `role` attributes are set beyond HTML defaults.

---

## 2. Keyboard accessibility

| Key | Behaviour |
|---|---|
| `Tab` | Moves focus through suggestions (if any), textarea, submit button in DOM order |
| `Enter` (in textarea) | Submits prompt |
| `Shift+Enter` (in textarea) | Newline |
| `Enter`/`Space` (on submit button) | Submits prompt |
| `Enter`/`Space` (on suggestion chip) | Sets textarea to suggestion text |

---

## 3. Screen reader behaviour

- The `<textarea>` is announced with its `placeholder` text as the accessible name when no `<label>` is associated.
- The submit button is announced as disabled when `loading` is `true`; the label "Thinking…" provides context.
- Suggestion chip buttons are announced with their string content.
- The character count span (`{value.length} / {maxLength}`) is readable but has no `aria-live` — it will not be announced as it updates.

---

## 4. Known gaps

| Gap | Severity | Description | Disposition |
|---|---|---|---|
| No `<label>` for textarea | High | The `<textarea>` has no associated `<label>` element. `placeholder` serves as the accessible name but is not a substitute for a label. | Resolved in wave-N via `inputAttributes` (pass `aria-label` or `id` for `<label htmlFor>`) |
| No live region for char count | Medium | Counter updates silently. | Partially resolved in wave-N (§5.2) |
| No live region for loading | Medium | `loading → true` is not announced. | Resolved in wave-N (§5.3) |
| Silent maxLength reject | Low | No announcement when character is dropped. | Accepted-risk; wave-N does not add per-keystroke announcements |

---

## 5. Wave-N expansion — accessibility additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 5.1 Input labeling (resolves High gap)

`inputAttributes` (Semantic §8.7) is the primary mechanism. The host should pass either:
- `inputAttributes={{ 'aria-label': 'Describe your question' }}` — standalone label, or
- `inputAttributes={{ id: 'prompt-input' }}` paired with `<label htmlFor="prompt-input">` in the host.

The component must NOT suppress or override `aria-label`, `aria-labelledby`, or `aria-describedby` values provided via `inputAttributes`.

### 5.2 Character count live region

When `showCharCount={true}` AND `maxLength` is set:
- The character count span gains `aria-live="polite"` and debounces announcements to 2s (prevents per-keystroke spam).
- The textarea gains `aria-describedby` pointing to the char count span's `id`.

### 5.3 Loading live region

A visually hidden `role="status" aria-live="polite"` element is always present in the DOM. When `loading` transitions to `true`, it announces `"Generating response, please wait"`. When `loading` returns to `false`, it announces `"Response ready"`.

### 5.4 Attachment chip accessibility

Attachment chip list container: `role="list" aria-label="Attached files"`. Each chip: `role="listitem"`. File name span: plain text (screen readers read it as the listitem content). Remove (×) button: `aria-label="Remove {file.name}"`.

### 5.5 Speech to text button

Same pattern as Chat Accessibility §7.8: `aria-label="Start speech to text"` / `aria-label="Stop speech to text" aria-pressed="true"` during recording. `aria-live="polite"` status region adjacent to the button announces recording state.

### 5.6 onFocus / onBlur announcements

No ARIA changes needed for `onFocus`/`onBlur` — these are passthrough events; the browser already manages focus announcement for `<textarea>` and `<input>` elements.

### 5.7 readOnly mode

When `readOnly={true}`, the textarea gains `aria-readonly="true"` (in addition to the HTML `readOnly` attribute). Attachment chips in read-only mode have no remove button, so no interactive ARIA attributes are needed on the chip itself.

### 5.8 P1 gaps resolved by wave-N spec

| Gap ID | Severity | Description | Resolution |
|---|---|---|---|
| G-PB-A1 | P1 | No `aria-label` prop on textarea | `inputAttributes` (§5.1) |
| G-PB-A2 | P1 | No `onFocus`/`onBlur` (FR-2 missing) | Semantic §8.7 + Interaction §8.5 |
| G-PB-A3 | P1 | `fillMode` missing (FR-3) | Semantic §8.7 (`fillMode` adopted) |
