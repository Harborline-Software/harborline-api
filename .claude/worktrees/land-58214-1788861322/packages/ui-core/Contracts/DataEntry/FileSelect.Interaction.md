# FileSelect — Interaction Contract

- **Component:** FileSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FileSelect.Semantic.md) · [Interaction](./FileSelect.Interaction.md) · [Accessibility](./FileSelect.Accessibility.md) · [Styling](./FileSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileSelect.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

FileSelect has a single interaction loop: button click → OS file picker →
`onSelect(files)`. This contract documents that loop and the disabled mode.

---

## 2. Click activation

1. User clicks (or activates via keyboard) the visible `<button>`.
2. The button's `onClick` handler calls `inputRef.current?.click()`.
3. The hidden `<input type="file">` fires the OS file picker.
4. After user selects files, `handleChange` runs.

---

## 3. File handling

`handleChange(e)`:

1. Converts `e.target.files` to `File[]`.
2. Filters: keeps only files where `!maxFileSize || f.size <= maxFileSize`.
3. Calls `onSelect?.(files)` — only when at least one file passes (empty array is
   possible if all files are filtered; `onSelect` is called with an empty array
   in that case — a minor inconsistency vs DocumentUploadZone which does not
   call `onFiles` on empty).
4. Resets `inputRef.current.value = ''` to allow re-selecting the same file.

---

## 4. Disabled mode

When `disabled === true`:

- The button has the native `disabled` attribute — non-interactive, non-focusable.
- The hidden input also has `disabled` — prevents programmatic activation.
- Visual: `disabled:pointer-events-none disabled:opacity-50`.

---

## 5. Keyboard behaviour

The button is a native `<button type="button">`:

| Key | Behaviour |
|---|---|
| Tab | Focus the button. |
| Enter / Space | Activate the button (opens file picker). |

---

## 6. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | `onSelect` is called with empty array when all files filtered — inconsistent with DocumentUploadZone | Host must guard for empty array |
| G2 | No error feedback for over-limit files | Silent drop |
