# PDFViewer — Accessibility Contract

- **Component:** PDFViewer
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFViewer.Semantic.md) · [Interaction](./PDFViewer.Interaction.md) · [Styling](./PDFViewer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFViewer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `title="PDF viewer"` | `<iframe>` | Names the iframe for AT |
| `aria-label` | Toolbar buttons | Names page and zoom controls independently of their glyphs |
| `aria-live="polite"` | Page and zoom status | Announces toolbar-driven state changes |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-PV4 | High | PDF content inside the `<iframe>` is not accessible to AT — browser PDF viewers are opaque to the accessibility tree; users relying on AT cannot read the PDF content | Accepted-risk M1 (structural limitation of iframe-based PDF viewing) |
| G-PV5 | Medium | Toolbar buttons (‹, ›, −, +) had no accessible names beyond their glyphs | Resolved 2026-07-15: controls have descriptive `aria-label` values |
| G-PV6 | Medium | Page and zoom changes were not announced | Resolved 2026-07-15: both status values are polite live regions |
| G-PV7 | Low | "Open" link did not describe its PDF destination or new-tab behavior | Resolved 2026-07-15: the link has a descriptive accessible name |
