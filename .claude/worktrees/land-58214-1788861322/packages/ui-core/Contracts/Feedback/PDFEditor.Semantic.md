# PDFEditor — Semantic Contract

- **Component:** PDFEditor
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PDFEditor.Interaction.md) · [Accessibility](./PDFEditor.Accessibility.md) · [Styling](./PDFEditor.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFEditor.tsx` (not yet implemented)
- **Catalog row:** #A19 PDFEditor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Purpose

PDFEditor extends PDFViewer's display capabilities with three additional
functional layers: annotation (text highlight, underline, sticky note),
form filling (native PDF form fields via the pdfjs annotation layer), and
e-signature placement (an embedded Signature widget positioned at a
designated coordinate on a PDF page). An Export action produces a modified
PDF for download or programmatic consumption.

PDFEditor is intentionally a superset of PDFViewer — every PDFViewer
capability (display, page navigation, zoom, text layer, annotation layer) is
preserved. Consumers that want display-only can continue using PDFViewer;
PDFEditor is for surfaces that need user interaction with the document.

---

## 2. Data model

```typescript
// ---- Annotation types ----

/** The tool currently active in the editor toolbar. */
type PDFEditorTool = 'read' | 'highlight' | 'underline' | 'sticky-note' | 'sign' | 'form'

/** A closed set of annotation kinds stored by the component. */
type PDFAnnotationKind = 'highlight' | 'underline' | 'sticky-note'

/** A single annotation placed by the user on a PDF page. */
interface PDFAnnotation {
  id: string
  kind: PDFAnnotationKind
  /** 1-based page number */
  page: number
  /** Normalized rect [x1, y1, x2, y2] in PDF user-space units (origin = bottom-left) */
  rect: [number, number, number, number]
  /** For sticky-note: the note body text */
  note?: string
  /** Tailwind-compatible color token, e.g. 'yellow', 'blue' */
  color?: string
}

// ---- Signature placement ----

/** Placement for an e-signature zone on a specific PDF page. */
interface PDFSignatureZone {
  /** 1-based page number */
  page: number
  /** Normalized rect [x1, y1, x2, y2] in PDF user-space units */
  rect: [number, number, number, number]
}

// ---- Export result ----

/** The result of a user-triggered export. */
interface PDFEditorExportResult {
  /** Blob containing the modified PDF bytes. */
  blob: Blob
  /** Pre-computed object URL (caller should revoke after use). */
  objectUrl: string
}

// ---- Main props ----

interface PDFEditorProps {
  // -- Source (identical surface to PDFViewerProps) --
  data?: ArrayBuffer | Uint8Array | string
  url?: string

  // -- Controlled page / zoom (identical to PDFViewerProps) --
  page?: number
  defaultPage?: number
  onPageChange?: (page: number) => void
  zoom?: number

  // -- Dimensions / layout (identical to PDFViewerProps) --
  height?: string | number
  width?: string | number
  className?: string

  // -- Editor-specific: annotations --
  /** Initial annotation set (controlled or seed). */
  annotations?: PDFAnnotation[]
  /** Called whenever the annotation set changes. */
  onAnnotationsChange?: (annotations: PDFAnnotation[]) => void

  // -- Editor-specific: form fields --
  /**
   * When true, renders native PDF form fields via the pdfjs annotation layer
   * and allows the user to fill them. Form-field values are NOT surfaced
   * separately from export; the filled PDF blob is the authoritative output.
   */
  formFilling?: boolean

  // -- Editor-specific: signature --
  /** Defines where on the PDF the signature widget renders (optional). */
  signatureZone?: PDFSignatureZone
  /** Current signature data URI or SVG string (controlled). */
  signatureValue?: string
  /** Called after each signature stroke or clear. */
  onSignatureChange?: (value: string) => void

  // -- Editor-specific: export --
  /** Called when the user triggers export; receives the result blob and object URL. */
  onExport?: (result: PDFEditorExportResult) => void
  /**
   * Text for the export button. Defaults to 'Export PDF'.
   */
  exportLabel?: string

  // -- Tool control --
  /** Controlled active tool. */
  tool?: PDFEditorTool
  /** Default tool on mount. Defaults to 'read'. */
  defaultTool?: PDFEditorTool
  /** Called when the user changes tool. */
  onToolChange?: (tool: PDFEditorTool) => void

  // -- Read-only / disabled --
  /**
   * When true, annotation and signing tools are disabled (display only).
   * Form filling is also disabled. Equivalent to PDFViewer in read-only mode.
   */
  readOnly?: boolean
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `data` | `ArrayBuffer \| Uint8Array \| string` | — | In-memory PDF. Passed through to pdfjs `Document`. Mutually exclusive with `url`; `url` takes precedence. |
| `url` | `string` | — | Remote or local-origin PDF URL. |
| `page` | `number` | — | Controlled current page. |
| `defaultPage` | `number` | `1` | Initial page on mount when `page` is uncontrolled. |
| `onPageChange` | `(page: number) => void` | — | Fires on toolbar page navigation. |
| `zoom` | `number` | `1` | Initial zoom level (1 = 100%). |
| `height` | `string \| number` | `'800px'` | Container height. Numbers treated as px. |
| `width` | `string \| number` | `'100%'` | Container width. |
| `className` | `string` | — | Additional root classes. |
| `annotations` | `PDFAnnotation[]` | `[]` | Annotations to render. When provided, the component is in controlled annotation mode; internal state is ignored for the rendered set. |
| `onAnnotationsChange` | `(annotations: PDFAnnotation[]) => void` | — | Fired after any add / update / delete of an annotation. |
| `formFilling` | `boolean` | `false` | Enables native PDF form fields. |
| `signatureZone` | `PDFSignatureZone` | — | When provided, renders an embedded Signature widget at the specified PDF-space rect on the target page. |
| `signatureValue` | `string` | — | Controlled signature value (SVG or data URL from the Signature component). |
| `onSignatureChange` | `(value: string) => void` | — | Fired on each signature stroke / clear. |
| `onExport` | `(result: PDFEditorExportResult) => void` | — | Fired when user activates the Export button. |
| `exportLabel` | `string` | `'Export PDF'` | Export button label text. |
| `tool` | `PDFEditorTool` | — | Controlled active tool. |
| `defaultTool` | `PDFEditorTool` | `'read'` | Initial tool when `tool` is uncontrolled. |
| `onToolChange` | `(tool: PDFEditorTool) => void` | — | Fired on tool-selector change. |
| `readOnly` | `boolean` | `false` | Disables all editing; component behaves as PDFViewer. |

---

## 4. PDF source

Same semantics as PDFViewer: `url` > `data`. Blob URLs created from `data`
are revoked on unmount. The source is passed to `react-pdf` `<Document>`
which uses `pdfjs-dist` as the rendering engine.

---

## 5. Annotations

### 5.1 Storage

Annotations are stored as a flat `PDFAnnotation[]` keyed by `id`. Ids are
generated by the component (e.g., `crypto.randomUUID()`). The component
maintains internal state when `annotations` prop is not provided (uncontrolled)
and fires `onAnnotationsChange` on every mutation.

### 5.2 Supported kinds

| Kind | Description | Required fields | Optional fields |
|---|---|---|---|
| `highlight` | Yellow (or custom color) background over selected text | `page`, `rect` | `color` |
| `underline` | Underline drawn at the baseline of a text range | `page`, `rect` | `color` |
| `sticky-note` | A note icon anchored to a rect; opens a popover with `note` text | `page`, `rect` | `note`, `color` |

Annotation rendering is via a custom overlay `<div>` layer positioned
absolutely over the pdfjs `<Page>` canvas + text layer. Annotations are
NOT written into the PDF bytes until Export is triggered.

### 5.3 Coordinate system

Rects are stored in PDF user-space units (origin at bottom-left of page,
positive Y up) to stay aligned with the pdfjs rendering model. The overlay
layer must apply the affine transform (flip Y, scale to rendered size) when
positioning annotation divs over the page canvas.

---

## 6. Form filling

When `formFilling={true}`, pdfjs renders interactive form elements (text
inputs, checkboxes, radio buttons, select) via the annotation layer. The
component enables `renderAnnotationLayer` and sets `renderForms` (pdfjs flag)
to `true`. Changed form-field values are committed to the pdfjs
`PDFFormData` structure; they are flushed into the exported blob on Export.

---

## 7. Signature slot

When `signatureZone` is provided, the component renders a Signature widget
(`Signature` component from DataEntry family) positioned as an overlay at
the coordinates defined by `signatureZone.rect` on `signatureZone.page`.

The overlay is visible only when the active page matches `signatureZone.page`.
The Signature widget is embedded in a `<div>` that is positioned absolutely
over the page canvas via the same coordinate transform used for annotations.

`signatureValue` / `onSignatureChange` are forwarded to the embedded
`<Signature>` component's `value` / `onChange` props. When `signatureValue`
is a non-empty string, the signature widget renders in "preview" (the stroke
is replayed); when empty, the canvas is clear.

---

## 8. Export

Export is triggered by the user clicking the Export toolbar button. The
component:

1. Merges annotations into the PDF bytes via `pdfjs-dist`'s write API
   (planned; may fall back to download of original bytes in early implementations).
2. Embeds the filled form-field values (if `formFilling` was active).
3. Rasterizes the signature stroke onto the designated page at the signature
   zone coordinates (if `signatureZone` is provided and `signatureValue` is
   non-empty).
4. Produces a `Blob` + pre-computed `objectUrl`.
5. Fires `onExport(result)`.
6. Also triggers an automatic browser download of the blob (`<a download>`).

If no export mutation has occurred (no annotations, no form fills, no
signature), the export produces a copy of the original PDF bytes.

---

## 9. Active tool semantics

| Tool value | Description | What it enables |
|---|---|---|
| `'read'` | Pan / scroll mode — no annotation capture | Default; no overlay interaction |
| `'highlight'` | Text selection → highlight annotation | Mouse-select on text layer → `highlight` annotation added |
| `'underline'` | Text selection → underline annotation | Mouse-select on text layer → `underline` annotation added |
| `'sticky-note'` | Click to place a sticky note | Single click on page → `sticky-note` annotation at click point; popover opens for text entry |
| `'sign'` | Activate the signature widget | Switches to the signature zone page; signature widget becomes interactive |
| `'form'` | Focus the form layer | Tab focus enters the first form field on the current page |

---

## 10. Variants and states

| Variant / State | Description |
|---|---|
| **Read mode** | `tool === 'read'` or `readOnly === true`; no editing |
| **Annotating** | One of highlight / underline / sticky-note tool active |
| **Form filling** | `tool === 'form'`; form inputs are active |
| **Signing** | `tool === 'sign'`; Signature widget is interactive |
| **Loading** | PDF bytes being loaded; spinner + loading state shown |
| **Error** | PDF failed to load; error state shown |
| **No source** | Neither `data` nor `url` provided; empty state shown |

---

## 11. Composition examples

### View + annotate an invoice

```tsx
const [annotations, setAnnotations] = React.useState<PDFAnnotation[]>([])

<PDFEditor
  url="https://cdn.example.com/invoice.pdf"
  annotations={annotations}
  onAnnotationsChange={setAnnotations}
  defaultTool="highlight"
/>
```

### View + sign a lease agreement

```tsx
const [sig, setSig] = React.useState('')

<PDFEditor
  data={leasePdfBuffer}
  signatureZone={{ page: 4, rect: [72, 72, 288, 144] }}
  signatureValue={sig}
  onSignatureChange={setSig}
  onExport={({ blob }) => uploadSignedLease(blob)}
  exportLabel="Sign & Submit"
/>
```

### Read-only display (same as PDFViewer behaviour)

```tsx
<PDFEditor url={url} readOnly />
```

---

## 12. Deferred features

- **Multi-page annotation navigation** — current spec covers single-page-visible rendering; virtualized page lists are out of scope.
- **Annotation persistence format** — spec does not define a server-side storage schema for `PDFAnnotation[]`; that is a consumer concern.
- **PDF/UA tagged-PDF struct-tree overlay** — inherits PDFViewer's Phase 3 deferral (react-pdf issue #1494).
- **Undo / redo** — annotation history stack deferred to a later wave.
- **Collaborative annotations** — multiple concurrent editors out of scope.
- **`pdfjs-dist` write API maturity** — annotation-merge-into-PDF-bytes depends on the `pdfjs-dist` write/modify API which is under active development; early implementation may export the original bytes with annotations as a separate JSON sidecar.
- **`signatureValue` / `defaultValue` hydration** — inherits the Signature component's known gap (canvas starts blank; value hydration is not yet implemented).
