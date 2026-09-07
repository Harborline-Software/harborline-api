# Editor — Semantic Contract

- **Component:** Editor
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Editor.Interaction.md) · [Accessibility](./Editor.Accessibility.md) · [Styling](./Editor.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Editor.tsx`
- **Catalog row:** #51 Editor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** see RichTextEditor — Editor is an alias for RichTextEditor

---

## 1. Component purpose

**Editor** — a rich text / markdown editor with a toolbar and a resizable textarea. In M1, editing is markdown-based: toolbar buttons insert markdown syntax around selected text. The `<textarea>` is the primary edit surface; there is no contenteditable or WYSIWYG layer.

---

## 2. Props

```typescript
type EditorMode = 'rich' | 'markdown'

interface EditorProps {
  value?: string                  // controlled
  defaultValue?: string           // uncontrolled seed; default: ''
  onChange?: (value: string) => void
  placeholder?: string            // default: 'Type here…'
  tools?: Array<
    'bold' | 'italic' | 'underline' | 'strikethrough' |
    'ol' | 'ul' | 'indent' | 'outdent'
  >                               // default: ['bold','italic','underline','ol','ul']
  height?: number | string        // min-height of textarea; default: 200
  disabled?: boolean              // default: false
  mode?: EditorMode               // default: 'rich' (M1: no behavioral difference)
  className?: string
}
```

---

## 3. Toolbar tools

| Tool | Markdown applied |
|---|---|
| `bold` | `**selected**` |
| `italic` | `_selected_` |
| `underline` | `<u>selected</u>` |
| `strikethrough` | `~~selected~~` |
| `ul` | Prefixes each selected line with `- ` |
| `ol` | Prefixes each selected line with `1. `, `2. `, etc. |
| `indent` | No-op in M1 (no MARKERS entry) |
| `outdent` | No-op in M1 (no MARKERS entry) |

---

## 4. Controlled vs uncontrolled

`value !== undefined` = controlled (textarea uses `value` prop). Uncontrolled uses `defaultValue` on the textarea directly (React uncontrolled pattern). Toolbar operations work in both modes by reading `textarea.value` via ref.

---

## 5. Mode

`mode` prop is accepted but has no behavioral difference in M1 (no rendering/preview toggle). Reserved for a future WYSIWYG mode.

---

## 6. Kendo Editor re-audit (2026-06-11)

> Context: the kendo-spec-audit of 2026-06-11 originally marked Editor as "0% coverage / no contract / no impl" (a false negative — the CORRECTION note in that audit confirms all four contracts and a shipped implementation exist). This section re-audits the actual Kendo Editor API (`@progress/kendo-react-editor`) against the M1 contract above and records the gap delta.

### 6.1 What M1 covers vs Kendo API

| Kendo prop / feature | M1 contract coverage | Gap verdict |
|---|---|---|
| `value` / `defaultContent` / `onChange` | Covered (`value`, `defaultValue`, `onChange` in §2) | Covered |
| `tools` — toolbar tool list | Covered (§2 `tools` array; subset of Kendo's full set) | Partial — M1 subset is adequate for M1; Kendo adds `link`, `image`, `table`, `viewHtml`, `formatBlock` etc. |
| Markdown-insert toolbar (bold/italic/ul/ol/u/s) | Covered via MARKERS / list logic (§3, Interaction §1) | Covered for M1 tool subset |
| `disabled` | Covered (§2) | Covered |
| `height` | Covered as `min-height` (§2, Styling §4) | Partial — Kendo's `height` is fixed height, not min-height |
| `placeholder` | Covered (§2) | Covered |
| `className` | Covered (§2) | Covered |
| `onBlur` / `onFocus` | NOT in M1 contract | GAP — P1 (FR-2 adoption required) |
| `aria-label` / `ariaDescribedBy` / `ariaLabelledBy` | NOT in M1 contract (Gap G-ED5) | GAP — P1 (per existing accessibility gap) |
| `readOnly` | NOT in M1 contract | GAP — P2 |
| `keyboardNavigation` (Ctrl+B, etc.) | NOT in M1 (Gap G-ED1) | GAP — P2 (accepted-risk M1) |
| `contentStyle` | NOT in M1 | GAP — P2 (no contenteditable layer in M1 — moot until WYSIWYG mode) |
| `resizable` | Partially covered — textarea has `resize: vertical` via inline style (Styling §4) | Partial — not exposed as prop |
| `preserveWhitespace` | NOT in M1 | GAP — P2 |
| `onExecute` | NOT in M1 | GAP — P2 |
| `onMount` | NOT in M1 | GAP — P2 |
| `onIFrameInit` | NOT applicable (no iframe in M1 textarea model) | Moot for M1 |
| `onPasteHtml` | NOT in M1 | GAP — P1 (paste sanitization is important) |
| `defaultEditMode` (`div` vs `iframe`) | NOT applicable (M1 is textarea, not contenteditable) | Moot for M1 |
| Image paste + drag-drop + upload | NOT in M1 | GAP — deferred (requires WYSIWYG layer) |
| Find & Replace | NOT in M1 | GAP — deferred |
| Table insertion | NOT in M1 | GAP — deferred |
| Plugin system / schema | NOT in M1 | GAP — deferred |

### 6.2 P1 gaps to close in wave-N expansion

The following gaps are P1 per the audit rules and require a wave-N expansion section:

```typescript
// Wave-N additions to EditorProps
interface EditorPropsExpansion {
  /** FR-2: fires when the textarea receives focus */
  onFocus?: React.FocusEventHandler<HTMLTextAreaElement>
  /** FR-2: fires when the textarea loses focus */
  onBlur?: React.FocusEventHandler<HTMLTextAreaElement>
  /** Accessible label for the textarea (preferred over wrapping in <label>) */
  ariaLabel?: string
  /** aria-describedby pointing to an external description element */
  ariaDescribedBy?: string
  /** aria-labelledby pointing to an external label element */
  ariaLabelledBy?: string
  /** Read-only mode: textarea is not editable; toolbar buttons are disabled */
  readOnly?: boolean
  /** Intercept paste events to sanitize HTML before insertion into the textarea */
  onPasteHtml?: (html: string) => string
}
```

**`onPasteHtml`** — when a user pastes HTML-rich content into the textarea (e.g., from another web page), the browser's paste event carries HTML in `event.clipboardData`. In M1's plain textarea, this arrives as plain text automatically (no special handling needed). `onPasteHtml` is a forward-spec hook for a future WYSIWYG surface where the insertion point processes HTML — it is a P1 gap in the Kendo sense but a P2 gap in M1 practice (moot until contenteditable). Mark as deferred in wave-N.

### 6.3 Wave-N expansion section (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

**FR-2 adoption (onFocus / onBlur):**

- **`onFocus?: React.FocusEventHandler<HTMLTextAreaElement>`** — passes through to the underlying `<textarea>`. Per FR-2 (family-wide ruling 2026-06-11), all interactive inputs expose this pair.
- **`onBlur?: React.FocusEventHandler<HTMLTextAreaElement>`** — same passthrough.

**Accessibility props (resolves G-ED5):**

- **`ariaLabel?: string`** — sets `aria-label` on the `<textarea>`. Preferred when no external `<label>` is feasible.
- **`ariaDescribedBy?: string`** — sets `aria-describedby` on the `<textarea>`. Use to link validation messages or helper text.
- **`ariaLabelledBy?: string`** — sets `aria-labelledby` on the `<textarea>`. Use when a heading or visible label element should name the editor.

**readOnly:**

- **`readOnly?: boolean`** — default `false`. When `true`, the textarea renders with `readOnly` and `aria-readonly="true"`. All toolbar buttons are disabled. The outer container does not receive `opacity-50` (readOnly is distinct from disabled — content remains high-contrast for reading).

**id:**

- **`id?: string`** — sets `id` on the `<textarea>`. Allows `<label htmlFor={id}>` association from the host. Pair with `ariaLabel` or external label.

**FR-3 adoption:**

Editor does NOT adopt `fillMode`, `rounded`, `themeColor`, or `size` at this wave — the component is a full editing surface, not a single-line input field. Appearance is governed by the Styling contract token set. `size` would be ambiguous (font size? editor height?); both are already controlled by the host's CSS and the `height` prop.

### 6.4 Verdict: audit coverage after re-audit

- **M1 contract is valid and correctly covers M1 behavior** — the original "0% / no contract" audit row was an auditor false negative (search miss).
- **Actual M1 coverage vs Kendo full API: ~35%** (value/tools/disabled/height/placeholder/className/mode covered; focus/blur/ARIA/readOnly/onPaste/rich-edit features absent).
- **The gap that matters for wave-N:** `onFocus`/`onBlur` (FR-2), `ariaLabel`/`ariaDescribedBy`/`ariaLabelledBy`/`id` (resolves G-ED5), `readOnly` (P2). All other gaps are deferred until WYSIWYG mode (contenteditable/iframe rendering layer) is scoped.
- **No implementation changes required** for this expansion — it is forward-spec only. Existing `Editor.tsx` remains the M1 reference implementation.
