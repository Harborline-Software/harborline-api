# PDFEditor — Accessibility Contract

- **Component:** PDFEditor
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFEditor.Semantic.md) · [Interaction](./PDFEditor.Interaction.md) · [Accessibility](./PDFEditor.Accessibility.md) · [Styling](./PDFEditor.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFEditor.tsx` (not yet implemented)
- **Catalog row:** #A19 PDFEditor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. WCAG 2.1 AA target

PDFEditor targets WCAG 2.1 AA for all component-owned UI. PDF content
accessibility depends on source PDF quality (see §6 known gaps). The ONR
research document (`PDFViewer-accessibility-research.md`) is the authoritative
reference for the canvas + text-layer + annotation-layer architecture that
underpins both PDFViewer and PDFEditor.

---

## 2. DOM landmark structure

```
<section role="region" aria-label="PDF editor: {documentLabel}">
  <div role="toolbar" aria-label="PDF editing tools">
    <!-- tool selector + page nav + zoom + export -->
  </div>
  <div role="document" aria-label="PDF content">
    <!-- react-pdf <Document> + <Page> stack -->
    <!-- annotation overlay layer -->
    <!-- signature zone overlay (when signatureZone provided) -->
  </div>
</section>
```

`documentLabel` is derived from the filename of `url`, the `aria-label`
prop if provided, or defaults to `'PDF document'`.

---

## 3. ARIA roles and attributes

### 3.1 Root container

| Attribute | Value | Rationale |
|---|---|---|
| `role` | `"region"` | Named landmark; lets AT users jump in and out of the editor via rotor / landmark navigation (SC 2.4.1) |
| `aria-label` | `"PDF editor: {documentLabel}"` | Distinguishes this region from other page landmarks (SC 4.1.2) |

### 3.2 Toolbar

| Attribute | Value | Rationale |
|---|---|---|
| `role` | `"toolbar"` | Identifies the control group as a toolbar; tells AT users roving tabindex is in effect (SC 4.1.2) |
| `aria-label` | `"PDF editing tools"` | Toolbar accessible name (SC 4.1.2) |

Tool-selector buttons within the toolbar use the **roving tabindex pattern**:
only the active tool button has `tabIndex={0}`; all others have `tabIndex={-1}`.
Arrow keys (`ArrowLeft` / `ArrowRight`) cycle between buttons; Space / Enter
activate. This matches the ARIA Authoring Practices Guide toolbar pattern.

| Button attribute | Value | Notes |
|---|---|---|
| `aria-pressed` | `"true"` or `"false"` | Communicates which tool is active (SC 4.1.2) |
| `aria-label` | Tool name (e.g., `"Highlight text"`) | Icon-only buttons must have text alternative (SC 1.1.1, SC 4.1.2) |
| `aria-disabled` | `"true"` when `readOnly` | Non-Read tools disabled in readOnly mode; kept in tab order as `aria-disabled` not `disabled` so AT can announce availability |

### 3.3 Page navigation controls

| Element | Attribute | Value |
|---|---|---|
| Previous button | `aria-label` | `"Previous page"` |
| Next button | `aria-label` | `"Next page"` |
| Previous button | `aria-disabled` | `"true"` when on page 1 |
| Next button | `aria-disabled` | `"true"` when on last page (numPages known) |
| Page number input | `aria-label` | `"Page number, {currentPage} of {numPages}"` |
| Page status span | `aria-live` | `"polite"` — announces page change to AT (SC 4.1.3) |
| Page status span | `aria-atomic` | `"true"` — the full "Page N of M" string is announced atomically |

### 3.4 Zoom controls

| Element | Attribute | Value |
|---|---|---|
| Zoom out | `aria-label` | `"Zoom out"` |
| Zoom in | `aria-label` | `"Zoom in"` |
| Zoom display | `aria-live` | `"polite"` — announces zoom level change |
| Zoom display | `aria-label` | `"Zoom level: {pct}%"` — explicit label for AT |

### 3.5 Export button

| Attribute | Value |
|---|---|
| `aria-label` | Matches `exportLabel` prop text (no separate label needed if visible text is descriptive) |
| `aria-busy` | `"true"` while export is in progress |
| `aria-describedby` | Points to the export-status span (success / error) |

Export status span:

| Attribute | Value |
|---|---|
| `role` | `"status"` (success) or `"alert"` (error) |
| `aria-live` | `"polite"` for success; `"assertive"` for error |

### 3.6 Document / page area

| Element | Attribute | Value |
|---|---|---|
| Document wrapper | `role` | `"document"` |
| Document wrapper | `aria-label` | `"PDF content"` or `"{documentLabel}"` |
| Page canvas | `aria-hidden` | `"true"` — canvas is visual only; text layer is the AT-readable surface |
| Text layer container | `role` | `"presentation"` (react-pdf default; individual spans are text nodes) |

### 3.7 Annotation overlays

Each annotation overlay `<div>`:

| Attribute | Value |
|---|---|
| `role` | `"img"` for highlight/underline; `"note"` is not a valid ARIA role — use `role="img"` with `aria-label` |
| `aria-label` | `"Highlight on page {N}"` / `"Underline on page {N}"` / `"Note: {noteText} on page {N}"` |
| `tabIndex` | `0` — annotations are keyboard-focusable for selection/deletion |

Sticky-note popover:

| Attribute | Value |
|---|---|
| `role` | `"dialog"` |
| `aria-label` | `"Edit note"` |
| `aria-modal` | `"true"` |

Focus management for sticky-note popover: on open, move focus to the
`<textarea>`; on close (Escape or commit), return focus to the annotation
overlay that triggered it.

### 3.8 Signature zone overlay

The signature zone wrapper:

| Attribute | Value |
|---|---|
| `role` | `"group"` |
| `aria-label` | `"Signature area"` |

The embedded Signature component retains its own accessibility attributes
per the Signature Accessibility contract. When `tool !== 'sign'` and
`signatureValue` is non-empty, the canvas renders in preview mode:

| Attribute | Value |
|---|---|
| `aria-label` | `"Signature provided"` |
| `role` | `"img"` |

When the signature zone is empty and tool is not `'sign'`:

| Attribute | Value |
|---|---|
| `aria-label` | `"Signature area — empty"` |
| `role` | `"img"` |

### 3.9 Load states

| State | Element | Attributes |
|---|---|---|
| Loading | `<div>` | `role="status"`, `aria-label="Loading PDF"`, `aria-live="polite"` |
| Error | `<div>` | `role="alert"`, `aria-live="assertive"` |
| No source | `<div>` | No special role; static text |

---

## 4. Keyboard navigation map

| Context | Key | Behavior |
|---|---|---|
| Toolbar (tool selector) | `Tab` | Enters toolbar; focuses active tool button |
| Toolbar (tool selector) | `ArrowLeft` / `ArrowRight` | Cycle between tool buttons (roving tabindex) |
| Toolbar (tool selector) | `Space` / `Enter` | Activate focused tool |
| Toolbar (page nav) | `Tab` | Move between toolbar controls in DOM order |
| Toolbar (page input) | Type + `Enter` | Navigate to typed page number |
| Document | `Tab` | Enter the document from toolbar; move through focusable text-layer elements (links, form fields) |
| Document | `Shift+Tab` | Return to toolbar from document |
| Annotation overlay | `Tab` | Move between focusable annotations |
| Annotation overlay (selected) | `Delete` / `Backspace` | Delete the focused annotation |
| Annotation overlay (selected) | `Enter` / `Space` | Open annotation popover (edit for sticky-note; delete confirm for highlight/underline) |
| Sticky-note popover | `Escape` | Close without saving (new annotation) or close without changes (existing) |
| Sticky-note popover | `Enter` (blur or button) | Commit text; close popover; return focus to annotation overlay |
| Sign tool active | `Tab` | Move focus into signature widget |
| Signature widget | Keyboard behavior per Signature Accessibility contract |
| Any tool active | `Escape` | Cancel in-progress gesture; return tool to Read mode |

---

## 5. WCAG 2.1 AA criteria addressed

| Criterion | Level | How PDFEditor addresses it |
|---|---|---|
| SC 1.1.1 Non-text Content | A | Canvas rendered `aria-hidden`; text layer provides text equivalent; annotation overlays have `aria-label`; signature preview has `role="img" aria-label` |
| SC 1.3.1 Info and Relationships | A | Toolbar uses `role="toolbar"`; document uses `role="document"`; region uses `role="region"`; sticky-note dialog uses `role="dialog"` |
| SC 2.1.1 Keyboard | A | All toolbar controls, annotation overlays, and signature widget are keyboard-operable; roving-tabindex on tool selector; Tab nav through document |
| SC 2.1.2 No Keyboard Trap | A | Sticky-note dialog returns focus on Escape; signature zone Tab-navigable; no modal that cannot be Escaped |
| SC 2.4.1 Bypass Blocks | A | `role="region"` landmark allows AT users to skip to / past the editor via landmark navigation |
| SC 2.4.3 Focus Order | A | Focus order follows logical DOM order: toolbar → document → annotations; roving tabindex on tool group |
| SC 2.4.7 Focus Visible | AA | Tailwind `focus-visible:ring-2 focus-visible:ring-ring` on all interactive elements |
| SC 4.1.2 Name, Role, Value | A | Every interactive element has accessible name and role; `aria-pressed` on tool buttons; `aria-disabled` on disabled controls; `aria-busy` during export |
| SC 4.1.3 Status Messages | A | Page change announced via `aria-live="polite"`; zoom change announced; export status announced; load states announced |
| SC 1.4.3 Contrast | AA | Design token colors meet 4.5:1 (text) / 3:1 (UI components); see Styling contract §5 |
| SC 1.4.10 Reflow | AA | PDF pages rendered via pdfjs at configurable zoom; `page-width` scale mode available for reflow at 400% |

---

## 6. Known gaps

| Gap ID | Severity | WCAG criterion | Description | Disposition |
|---|---|---|---|---|
| G-PEA1 | High | SC 1.3.1 (A) | Struct-tree overlay for tagged PDFs not yet first-class in `react-pdf` (issue #1494 closed without merge). Heading hierarchy, list structure, table semantics within the PDF are not exposed to AT for untagged PDFs. | Inherited from PDFViewer G-PV4 path; ONR research §8. Phase 3 forward-spec: `customTextRenderer` + `includeMarkedContent` overlay. Accepted-risk for untagged PDFs in v1. |
| G-PEA2 | High | SC 1.1.1 (A) | Scanned-image PDFs (no text layer) produce a blank text layer — AT sees an empty document. The component will surface a visible + announced warning per ONR §8, but content remains inaccessible. | Same as PDFViewer G-PV4 analog. Warning is the only viable mitigation without server-side OCR. Accepted-risk v1. |
| G-PEA3 | Medium | SC 2.1.1 (A) | Annotation placement via mouse-drag text selection is inherently pointer-dependent. A keyboard-only path for placing highlight / underline annotations is not specified in this version. | Accepted-risk v1. Keyboard annotation placement (e.g., via selected text + Shift+H) deferred to accessibility hardening wave. |
| G-PEA4 | Medium | SC 4.1.2 (A) | Annotation overlay rects are positioned with absolute pixel values derived from PDF user-space → screen-space transform. If the transform calculation has a rounding error, overlays may not visually align with the text they annotate — reducing the utility of `aria-label` references to page content. | Implementation must verify overlay alignment at each zoom level. Testing requirement. |
| G-PEA5 | Low | SC 1.3.1 (A) | `role="img"` on annotation overlays is a pragmatic choice; the more semantically precise role would be a custom annotation structure, but no ARIA role exists for "highlighted text range". | Accepted-risk; industry practice aligns with `role="img"` + `aria-label` for annotation overlays. |
| G-PEA6 | Low | SC 1.4.5 (AA) | Password-protected PDFs require a UI prompt; this is deferred per ONR §8. | Deferred; current scope is Harborline-generated unencrypted PDFs. |
| G-PEA7 | Low | SC 2.4.6 (AA) | Struct-tree dependency on PDF source quality: only tagged PDFs (PDF/UA) produce heading-rich reading experience. Harborline controls its PDF generation pipeline and should emit PDF/UA-tagged documents — this is a follow-up directive against the PDFExport pipeline, not a PDFEditor gap per se. | Noted as follow-up for PDFExport pipeline (not blocking PDFEditor acceptance). |

---

## 7. Testing requirements

### Automated (axe / pa11y)

- `role="region"` with `aria-label` — axe rule `region`
- `role="toolbar"` with `aria-label` — axe rule `aria-required-attr`
- All toolbar buttons have `aria-label` — axe rule `button-name`
- `aria-pressed` present on tool-selector buttons — axe rule `aria-allowed-attr`
- `aria-live` on page-status span — axe rule `aria-valid-attr-value`
- Canvas has `aria-hidden="true"` — axe rule `canvas-caption` bypassed correctly
- Color contrast of toolbar UI ≥ 3:1, text ≥ 4.5:1 — axe rule `color-contrast`

### Manual (AT smoke tests)

- **VoiceOver (macOS):** Load an invoice PDF; confirm VoiceOver reads heading + body text from text layer via rotor Text navigation; confirm toolbar buttons announce names and pressed state; confirm page-change announcement fires when navigating.
- **NVDA (Windows):** Same test; confirm reading-order matches visual order on a multi-column invoice.
- **Keyboard-only (Chrome):** Tab into toolbar; cycle tools with arrow keys; Tab into document; Tab through form fields when `formFilling` enabled; Tab to annotations; Delete an annotation; Escape from sticky-note popover returns focus correctly.
- **Signature keyboard:** Activate Sign tool; Tab into Signature widget; confirm Signature keyboard behavior per Signature Accessibility contract.
- **Export:** Tab to Export button; activate with Enter; confirm `aria-busy` set during export; confirm success announcement.

### Bundle / integration

- Confirm `pdfjs-dist` chunk is dynamic-imported (route initial bundle unchanged).
- Confirm `pdf.worker.min.js` served from same origin (no `unpkg` in production config).
