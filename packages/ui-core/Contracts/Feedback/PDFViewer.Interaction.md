# PDFViewer — Interaction Contract

- **Component:** PDFViewer
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFViewer.Semantic.md) · [Accessibility](./PDFViewer.Accessibility.md) · [Styling](./PDFViewer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFViewer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Toolbar interactions

All interactions are in the optional toolbar (rendered when `toolbar=true`).

### Page navigation

| Trigger | Effect |
| --- | --- |
| "‹" Previous button click | `changePage(Math.max(1, currentPage - 1))` → updates iframe src + fires `onPageChange` |
| "›" Next button click | `changePage(currentPage + 1)` → updates iframe src + fires `onPageChange` |

Note: Previous button can decrement to page 1 (floor-clamped). Next button
has no upper bound — total page count is not known to the component.

### Zoom

| Trigger | Effect |
| --- | --- |
| "−" Zoom out click | `setCurrentZoom(z => Math.max(0.25, z - 0.25))` |
| "+" Zoom in click | `setCurrentZoom(z => Math.min(4, z + 0.25))` |

Zoom changes update the `transform: scale()` on the iframe and the
`#zoom=N%` hash in the iframe src.

### Open link

| Trigger | Effect |
| --- | --- |
| "Open" anchor click | Opens `objectUrl` in a new tab (`target="_blank"`) |

---

## 2. Controlled page

When `page` prop changes, `useEffect` fires and updates `currentPage` to
match. This is a one-way sync from host to component.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-PV1 | Medium | No upper bound on page navigation — Next can increment indefinitely past the last page; relies on browser PDF viewer to ignore invalid `#page=N` | Accepted-risk M1 |
| G-PV2 | Low | Zoom via `transform: scale()` on the iframe doesn't interact with the PDF viewer's native zoom — actual text size is fixed | Accepted-risk M1 |
| G-PV3 | Low | No keyboard shortcuts for page navigation or zoom | Accepted-risk M1 |
