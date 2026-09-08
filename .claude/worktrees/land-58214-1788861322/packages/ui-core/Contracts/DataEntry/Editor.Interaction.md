# Editor — Interaction Contract

- **Component:** Editor
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Editor.Semantic.md) · [Accessibility](./Editor.Accessibility.md) · [Styling](./Editor.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Editor.tsx`
- **Catalog row:** #51 Editor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Toolbar buttons

Each toolbar button uses `onMouseDown` (not `onClick`) with `e.preventDefault()` to prevent focus leaving the textarea before the selection is read. This is the correct pattern for editor toolbar buttons.

`applyTool(tool)` reads `textareaRef.current.selectionStart/End`, builds the modified string, and calls `onChange(next)`. For uncontrolled, it also writes `ta.value = next` directly and restores selection position.

---

## 2. Textarea `onChange`

Direct typing: `onChange?.(e.target.value)` — fires on every keystroke.

---

## 3. Controlled mode toolbar

In controlled mode (`value` provided), toolbar operations call `onChange(next)` — the parent must update `value` for the change to be reflected. The component does NOT write `ta.value` directly in controlled mode.

---

## 4. Selection preservation

After toolbar insertion, `ta.setSelectionRange(start + open.length, end + open.length)` restores the cursor/selection inside the inserted syntax. Only implemented for MARKERS tools (bold/italic/underline/strikethrough) — list tools (`ul`, `ol`) do not restore selection position.

---

## 5. Keyboard

Toolbar buttons are standard `<button>` elements — keyboard accessible via Tab + Enter/Space. No editor-specific shortcuts (no Ctrl+B for bold in M1).

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ED1 | Medium | No keyboard shortcuts (Ctrl+B, Ctrl+I, etc.) — toolbar-only markdown insertion | Accepted-risk M1 |
| G-ED2 | Low | `indent` and `outdent` tools render in the toolbar but are no-ops (no MARKERS entry) | Accepted-risk M1; these tools should be removed from defaults or implemented |
| G-ED3 | Low | List tools don't restore selection after insertion | Accepted-risk M1 |
