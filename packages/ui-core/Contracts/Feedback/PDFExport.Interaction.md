# PDFExport — Interaction Contract

- **Component:** PDFExport
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFExport.Semantic.md) · [Accessibility](./PDFExport.Accessibility.md) · [Styling](./PDFExport.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFExport.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Imperative export trigger

PDFExport renders no interactive UI of its own. The export action is triggered
imperatively by the host:

| Mechanism | Effect |
| --- | --- |
| `ref.current.save()` | Captures the wrapper `<div>`'s DOM, calls `exportToPDF` with configured options |
| `ref.current.save(customName)` | Same but overrides the filename |
| `usePDFExport().save()` | Calls `ref.current?.save(fileName)` via a memoized callback |

---

## 2. No display-time user interaction

PDFExport wraps `children` in a plain `<div>`. No buttons, toggles, or
overlays are rendered by PDFExport itself. All triggering UI (download button,
print icon) is the host's responsibility.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-PE1 | Low | No error feedback if `exportToPDF` fails (e.g., content too large, print library unavailable) | Accepted-risk M1 |
| G-PE2 | Low | No loading/progress state while export is in progress — host cannot show a spinner | Accepted-risk M1 |
