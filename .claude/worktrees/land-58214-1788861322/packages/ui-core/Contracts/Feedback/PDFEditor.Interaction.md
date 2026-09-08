# PDFEditor — Interaction Contract

- **Component:** PDFEditor
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFEditor.Semantic.md) · [Interaction](./PDFEditor.Interaction.md) · [Accessibility](./PDFEditor.Accessibility.md) · [Styling](./PDFEditor.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFEditor.tsx` (not yet implemented)
- **Catalog row:** #A19 PDFEditor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Toolbar — tool selector

The toolbar renders a tool-selector group (one button per tool) followed by
page-navigation controls, zoom controls, and the Export button.

### 1.1 Tool-selector buttons

| Button | Tool value | Trigger | Effect |
|---|---|---|---|
| Read | `'read'` | Click | Sets active tool to `'read'`; fires `onToolChange('read')`; clears any in-progress annotation gesture |
| Highlight | `'highlight'` | Click | Sets active tool to `'highlight'`; fires `onToolChange('highlight')` |
| Underline | `'underline'` | Click | Sets active tool to `'underline'`; fires `onToolChange('underline')` |
| Sticky Note | `'sticky-note'` | Click | Sets active tool to `'sticky-note'`; fires `onToolChange('sticky-note')` |
| Sign | `'sign'` | Click | Sets active tool to `'sign'`; fires `onToolChange('sign')`; if the current page ≠ `signatureZone.page`, navigates to that page |
| Form | `'form'` | Click | Sets active tool to `'form'`; fires `onToolChange('form')`; moves keyboard focus to the first form field on the current page |

Tool buttons are mutually exclusive — selecting one deselects the others.
The button for the active tool carries `aria-pressed="true"`.

Disabled state: when `readOnly={true}`, all tool buttons except Read are
disabled (`aria-disabled="true"`, `tabIndex={-1}`, no interaction).

### 1.2 Keyboard navigation for tool selector

| Key | Effect |
|---|---|
| `ArrowLeft` / `ArrowRight` | Cycle between tool buttons (roving tabindex pattern). |
| `Space` / `Enter` | Activate the focused tool button. |

---

## 2. Toolbar — page navigation

Inherits PDFViewer behavior:

| Trigger | Effect |
|---|---|
| Previous button click | Decrement current page; minimum = 1; fires `onPageChange` |
| Next button click | Increment current page; maximum = `numPages` (when known); fires `onPageChange` |
| Page input change | User types a page number in the `<input>`; on blur or Enter, clamps to `[1, numPages]` and navigates; fires `onPageChange` |

Page navigation during active annotation tool clears any in-progress text
selection gesture on the old page.

---

## 3. Toolbar — zoom

| Trigger | Effect |
|---|---|
| Zoom out click | `Math.max(0.25, zoom - 0.25)` |
| Zoom in click | `Math.min(4, zoom + 0.25)` |

Zoom is applied to the pdfjs `<Page>` `scale` prop directly (real PDF zoom,
not iframe `transform: scale()`). Annotation overlay and signature zone
overlay re-measure after scale change.

---

## 4. Toolbar — export

| Trigger | Effect |
|---|---|
| Export button click | Button enters loading state (`aria-busy="true"`); component begins PDF export pipeline; on completion fires `onExport(result)` + triggers auto-download; button returns to normal state |

If the export pipeline errors, the button returns to normal state and an
`role="alert"` error notice is rendered near the button.

---

## 5. Annotation interactions

### 5.1 Highlight and underline tools

Selection gesture on the text layer:

| Event | Effect |
|---|---|
| `mousedown` on text layer | Start of selection; records start point |
| `mousemove` (button held) | Extends selection highlight; visual rubber-band selection overlay |
| `mouseup` on text layer | Ends selection; computes the set of text-layer spans covered; derives the bounding `rect` in PDF user-space; creates a `PDFAnnotation` with the active kind (`highlight` or `underline`); fires `onAnnotationsChange` |
| `touchstart` / `touchmove` / `touchend` | Same gesture for touch devices |
| Click without drag | No annotation created; selection discarded |

If the selection contains no text-layer spans (user clicks on whitespace),
no annotation is created.

The newly created annotation is immediately rendered as an overlay on the
page without waiting for re-render from the parent (optimistic local state).

### 5.2 Sticky-note tool

| Event | Effect |
|---|---|
| Click on page canvas or text layer | Places a sticky-note annotation at the click point; opens an inline popover with a `<textarea>` for the note text |
| `Enter` or blur inside popover `<textarea>` | Commits the note text; fires `onAnnotationsChange`; popover closes |
| Escape inside popover | Discards the annotation (if just placed) or closes popover without saving changes (if editing an existing note) |

### 5.3 Annotation interaction (all kinds — when in read mode)

| Event | Effect |
|---|---|
| Hover over annotation overlay | Tooltip / popover shows annotation details (for sticky-note: displays note text); annotation highlights with `ring-2` |
| Click on sticky-note annotation | Opens the note popover in edit mode |
| Click on highlight / underline annotation | Selects the annotation (indicated by `ring-2 ring-primary`); shows a delete button in the popover |
| Delete button click in annotation popover | Removes the annotation from the list; fires `onAnnotationsChange` |
| `Delete` / `Backspace` key when annotation is selected | Removes the selected annotation; fires `onAnnotationsChange` |

### 5.4 Keyboard annotation shortcut (when a tool is active)

| Key | Effect |
|---|---|
| `Escape` | Cancels active in-progress annotation gesture; returns tool to Read mode |

---

## 6. Form filling interactions

When `formFilling={true}` and `tool === 'form'`:

| Event | Effect |
|---|---|
| Click on a form field | Focuses that field; standard browser / pdfjs input behavior |
| `Tab` | Moves focus to next form field in document order |
| `Shift+Tab` | Moves focus to previous form field |
| Type in text field | Updates pdfjs form data in memory |
| Click / Space on checkbox | Toggles checked state |
| Click / arrow-key on radio group | Selects the radio option |
| `Escape` | Moves focus back to the main toolbar |

Form-field values are stored in pdfjs memory until export; they are NOT
surfaced via props. Consumers must trigger export to capture filled values.

---

## 7. Signature interactions

When `tool === 'sign'` and `signatureZone` is provided:

| Event | Effect |
|---|---|
| Activate Sign tool | Component navigates to `signatureZone.page` (if not already there); the Signature widget at the zone becomes interactive |
| Draw in Signature widget | Standard Signature component draw behavior: `mousedown` → `mousemove` → `mouseup` strokes; fires `onSignatureChange` after each stroke end |
| Clear button in Signature widget | Clears all strokes; fires `onSignatureChange('')` |
| Click outside Signature widget (while Sign tool active) | Does not navigate away from Sign tool; focus returns to signature zone |

When `signatureValue` is non-empty and the current page is `signatureZone.page`,
the signature is rendered in the zone in preview mode (strokes replayed on
an internal Signature instance). Switching to a non-Sign tool leaves the
signature visible in preview mode but non-interactive.

---

## 8. PDF load lifecycle

| State | Visible affordance | Interactivity |
|---|---|---|
| Loading | Spinner with `role="status"` | Toolbar page / zoom disabled; tool selector visible but disabled |
| Load success | PDF page rendered | Full interactivity |
| Load error | `role="alert"` error message with retry button | Retry button only |
| No source | Empty state message | No interaction |

Retry button click re-triggers the `react-pdf` `<Document>` load.

---

## 9. readOnly posture

When `readOnly={true}`:

- Tool selector shows only the Read tool, or all tools visually present but
  all non-Read tools disabled (`aria-disabled`).
- Annotation gestures do not fire (mousedown / touchstart handlers are
  not registered).
- Signature widget is rendered in preview-only mode.
- Form fields are `disabled`.
- Export button is still present and functional (allows exporting the
  original bytes).

---

## 10. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PE1 | Medium | Text selection for annotation does not work on non-text-layer content (scanned PDFs have no text-layer spans to select). An empty-text warning is shown but the annotation tool cannot be used. | Forward-spec accepted-risk; scanned-PDF warn shown per ONR research §8. |
| G-PE2 | Medium | Annotation merge into PDF bytes depends on `pdfjs-dist` write API maturity; initial implementation may not embed annotations into the exported bytes (original PDF + annotation JSON sidecar). | Accepted-risk v1; annotation embedding tracked as a follow-up. |
| G-PE3 | Low | No undo / redo for annotation actions. | Deferred to future wave. |
| G-PE4 | Low | Multi-touch selection for annotation on iOS is not specified and may conflict with browser's default text selection behavior. | Accepted-risk v1. |
| G-PE5 | Low | Signature zone supports exactly one zone; multi-signer flows (multiple zones on multiple pages) are not addressable by the current props surface. | Deferred; extend `signatureZone` to `signatureZones` array in future wave. |
| G-PE6 | Low | Sticky-note popover position is not specified relative to annotation rect; implementation may clip near page edges. | Accepted-risk v1; implementation to use floating-ui or equivalent. |
