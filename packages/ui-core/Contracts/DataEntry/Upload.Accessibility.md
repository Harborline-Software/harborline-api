# Upload — Accessibility Contract

- **Component:** Upload
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Upload.Semantic.md) · [Interaction](./Upload.Interaction.md) · [Styling](./Upload.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Upload.tsx`
- **Catalog row:** #144 Upload (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `aria-hidden` (implicit true) | `<input type="file">` | Hidden from AT (`sr-only` + `aria-hidden`) |
| `tabIndex={-1}` | `<input type="file">` | Not keyboard-focusable |
| `type="button"` | "Select files" `<button>` | Prevents form submission |
| `type="button"` | "Upload" `<button>` | Prevents form submission |
| `disabled` | "Upload" `<button>` | When no selected files |
| `aria-hidden` | Upload SVG icon | Decorative |
| `aria-hidden` | File SVG icon | Decorative |
| `aria-hidden` | X SVG icon | Decorative |
| `aria-label="Remove {file.name}"` | Per-file remove `<button>` | Accessible remove label |

---

## 2. File list

Rendered as `<ul>` + `<li>` — correct list semantics for AT. Each list item contains: file icon, filename, file size, progress bar (uploading), error message (error), or "Done" text (success).

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-UPL5 | High | No `aria-label` on "Select files" button beyond the text label — no status announcement when upload state changes | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-UPL6 | High | Progress bar for uploading files has no `role="progressbar"` or `aria-valuenow` | **Blocking before production use** — silent async operation violates WCAG 4.1.3; fix is low cost (add role + aria-valuenow to progress element) |
| G-UPL7 | High | No `aria-live` region for upload status changes (success/error/progress) | **Blocking before production use** — AT users receive no feedback on upload completion or failure; add `role="status"` live region |
| G-UPL8 | Low | ~~`aria-label="Remove"`~~ — fixed: contract now specifies `aria-label="Remove {file.name}"` | Resolved in contract M0 |
