# PDFViewer accessibility — OSS landscape + recommended architecture

- **Author:** ONR
- **Requester:** Engineer (G-PV4 disposition)
- **Companion contracts:** [Accessibility](./PDFViewer.Accessibility.md) · [Semantic](./PDFViewer.Semantic.md) · [Interaction](./PDFViewer.Interaction.md) · [Styling](./PDFViewer.Styling.md)
- **Reference implementation:** the earlier repository's `packages/ui-react/src/components/feedback/PDFViewer.tsx`
- **Status:** Research draft — informs the M1→M2 migration ADR for PDFViewer
- **Scope:** OSS landscape (MIT/Apache-2.0 only); WCAG 2.1 AA criteria that apply to embedded PDFs in web apps; recommended architecture + concrete dependency pick; migration path from the current iframe implementation. Out of scope: tagged-PDF authoring guidance, server-side PDF accessibility remediation, OCR-for-scanned-PDFs (mentioned as a known limitation, not a recommended workstream).

---

## 1. Executive summary

- **The current `<iframe src="...#page=N">` implementation is a known WCAG 2.1 AA failure surface.** Browser-native PDF viewers (Chrome's, Edge's, Safari's) render PDF content into a shadow tree that AT cannot reliably traverse; on macOS Safari the PDF inside an iframe is effectively a black box to VoiceOver. The `title="PDF viewer"` on the iframe satisfies SC 4.1.2 (Name, Role, Value) **for the iframe element only** — it does NOT make the PDF content inside it perceivable. G-PV4 is a real Level A failure of SC 1.1.1, SC 1.3.1, and SC 2.1.1, not just an AA gap.
- **The canonical AT-accessible architecture is canvas (visual) + transparent text layer (selectable + readable by AT) + annotation layer (interactive elements) + struct-tree overlay (for tagged PDFs).** Mozilla PDF.js is the reference implementation of this pattern; every credible MIT/Apache-2.0 React wrapper builds on `pdfjs-dist`.
- **Recommended dependency: `react-pdf` v10.4.1 (MIT) by Wojciech Maj on top of `pdfjs-dist` v5.4.296 (Apache-2.0, Mozilla).** It is the only MIT-licensed, actively-maintained React wrapper with ~950K weekly npm downloads, React 19 support, and a text-layer + annotation-layer rendering pipeline. `@react-pdf-viewer/core` (Phuoc Nguyen) is **commercial-only for production use** and must be excluded per fleet MIT policy.
- **Bundle-size trade-off is real but manageable.** `pdfjs-dist` is ~840KB JS + ~700KB worker (about 1.5MB raw, ~500KB gzipped). The mitigations are (1) dynamic-import `react-pdf` so the chunk only loads when a `<PDFViewer>` actually mounts; (2) ship `pdf.worker.min.js` (roughly half the size of the dev worker); (3) host the worker on the same origin (avoid `unpkg`/`cdnjs` in production). This is comparable to the bundle cost of adopting `react-pdf-export` and is justified by the AT obligation.
- **PDF.js does not fully satisfy WCAG AA on its own.** Its text layer satisfies SC 1.1.1 (text alternative is the extracted text content) and SC 1.3.1 (selectable text in correct reading order) for *untagged* PDFs. **Tagged-PDF semantic structure (headings, lists, tables) is partially supported via `getTextContent({ includeMarkedContent: true })` and a struct-tree overlay** — this is the moving target. For our use case (Harborline renders invoices, statements, leases — documents we control), we can ensure source PDFs are PDF/UA-conformant tagged, which lets PDF.js's struct-tree overlay carry the semantic info to AT.

**Recommended direction:** replace the iframe with a `react-pdf` based implementation, dynamic-imported, with the text layer + annotation layer + a small custom ARIA landmark wrapper. Keep the existing `PDFViewerProps` surface unchanged so consumers don't break. Author this as a single ADR-0017-A1 amendment + reverse-spec the Accessibility contract from "Accepted-risk G-PV4" to "Resolved via text-layer rendering."

---

## 2. Current gap (what G-PV4 means technically)

### 2.1 Why an iframe is opaque to AT

The current implementation hands a `blob:` or `https:` URL to the browser's native PDF plugin via `<iframe src="...#page=N&zoom=...">`:

```tsx
<iframe
  src={src}
  title="PDF viewer"
  className="flex-1 w-full border-0"
  style={{ transform: `scale(${currentZoom})`, ... }}
/>
```

What the browser does next is **vendor-specific and not part of the accessibility tree the web page controls**:

- **Chrome / Edge** — PDF rendered by an internal PDF plugin (PDFium-based). The PDF's structure is exposed to AT only via the *browser's own* accessibility surface, not via the embedding page's DOM. Tab focus enters the iframe but a screen reader has no programmatic way to walk into the PDF's heading structure, follow its reading order, or escape back into the host page reliably.
- **Safari (macOS / iOS)** — Native Preview-based renderer. VoiceOver navigation into iframe-embedded PDFs has been historically unreliable; users frequently must "Open" the PDF in a new tab and use Preview directly.
- **Firefox** — Uses an in-browser PDF.js. This is the *closest* the iframe approach gets to working AT, but it's only one of four major browsers, and PDF.js's iframe-viewer accessibility itself has open issues ([mozilla/pdf.js#20114](https://github.com/mozilla/pdf.js/issues/20114), opened 2025-07-22, unresolved).

The net is that **AT users get inconsistent-to-zero access** depending on browser + OS + AT combination.

### 2.2 WCAG criteria failed by the iframe approach

The `title="PDF viewer"` on the iframe satisfies SC 4.1.2 *for the iframe element itself* (the iframe has an accessible name "PDF viewer"). It does not address the content of the PDF.

| Criterion | Level | What it requires | Status with iframe approach |
| --- | --- | --- | --- |
| SC 1.1.1 Non-text Content | A | All non-text content has a text alternative serving the equivalent purpose | FAIL — PDF rasterized inside the iframe has no programmatic text alternative reachable from the embedding DOM. The iframe `title` describes the *frame*, not the content. |
| SC 1.3.1 Info and Relationships | A | Information, structure, relationships conveyed visually can be programmatically determined | FAIL — heading hierarchy, list structure, table relationships inside the PDF are not exposed to the embedding page's accessibility tree. |
| SC 2.1.1 Keyboard | A | All functionality operable from keyboard | PARTIAL FAIL — toolbar buttons in the host work (G-PV5 notwithstanding) but in-document navigation (next-heading, next-link) is delegated to the browser's PDF plugin keystrokes, which are vendor-specific and undocumented. |
| SC 4.1.2 Name, Role, Value | A | UI components have programmatically-determinable name, role, value | PARTIAL PASS for the iframe element (`title="PDF viewer"`); FAIL for in-PDF interactive elements (form fields, links). |

All four are **Level A** — failure here means the component is not even WCAG 2.1 A conformant, never mind AA. The "High severity" label on G-PV4 is correct.

---

## 3. OSS landscape

All bundle sizes are gzip-min approximations from current npm/bundlephobia data at time of research (June 2026).

| Library | License | Engine | Last release | Weekly DL | Bundle (min+gz) | AT coverage | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **`react-pdf` (wojtekmaj)** v10.4.1 | **MIT** | `pdfjs-dist` 5.4.296 (Apache-2.0) | 2026-02-25 | ~950K | ~50KB wrapper + ~500KB pdfjs-dist + ~250KB worker (gz) | Text layer + annotation layer; struct-tree partial (issue #1494 closed without merge); good in practice for untagged PDFs | **RECOMMENDED** |
| **`pdfjs-dist`** v5.4.296 / v6.0.227 | **Apache-2.0** | Self (Mozilla PDF.js) | 2026-05 | very high | ~500KB + ~250KB worker (gz) | The reference implementation — canvas + text layer + annotation layer + experimental tagged-PDF struct tree; ongoing work in [#20114](https://github.com/mozilla/pdf.js/issues/20114) | Fallback / direct-use only if `react-pdf` doesn't fit |
| **`@react-pdf-viewer/core`** v3.12.0 (Phuoc Nguyen) | **Commercial** (paid license for production; OSS source visible) | `pdfjs-dist` | active | mid | ~80KB wrapper + pdfjs | Has accessibility hooks, plugin architecture | **EXCLUDED** — license incompatible with fleet MIT posture |
| **`@embedpdf/react-pdf-viewer`** | MIT | Custom WASM PDFium engine (not PDF.js) | 2026 | low | larger (WASM blob) | Not documented; no WCAG claims found | EXCLUDE for v1 — PDFium-WASM is unproven for our AT case and adds a non-trivial OSS engine swap; revisit only if `react-pdf` proves inadequate |
| **`@react-pdf-kit/viewer`** | MIT | `pdfjs-dist` | recent | low | similar to react-pdf | Similar text+annotation layer; smaller community than `react-pdf` | Watch — smaller community, fewer downloads; would only consider if `react-pdf` becomes unmaintained |
| **PSPDFKit / Nutrient** | Commercial | Proprietary | active | n/a | n/a | Excellent (it's their selling point) | EXCLUDED — commercial, instructive only |
| **PDF.js Express** | Commercial wrapper around PDF.js | `pdfjs-dist` | active | n/a | n/a | Same as PDF.js + commercial enhancements | EXCLUDED — commercial path is paid product |

**Key reads on licensing:**

- `react-pdf` (Wojciech Maj): MIT, confirmed at the package `package.json` and README.
- `pdfjs-dist` (Mozilla): Apache-2.0. Compatible with fleet MIT posture.
- `@react-pdf-viewer/core` (Phuoc Nguyen): the source is browseable on GitHub but the project page is explicit that **commercial use requires a paid license** ("you may use it multiple times in multiple projects, but you cannot re-distribute the Item as stock"). Despite frequent confusion with `react-pdf`, this is not an OSS-for-commercial path. **Do not adopt** for fleet code.
- `@embedpdf/react-pdf-viewer`: MIT-licensed but uses a custom PDFium-WASM engine instead of PDF.js. Promising but immature; AT story is undocumented in their marketing. **Not yet adoptable** as primary.

---

## 4. WCAG 2.1 AA requirements for embedded PDF content

The relevant Level A and AA criteria for a PDF viewer component:

### 4.1 Level A (mandatory minimum)

- **SC 1.1.1 Non-text Content (A)** — The visual rendering of a PDF page (canvas raster) is non-text content. The text-layer overlay serves as the text alternative: each visually-rendered character has a corresponding (visually invisible) DOM text node positioned to align with it. AT users read the text layer; sighted users see the canvas. *This is the architectural mechanism that makes PDF rendering AT-accessible.*
- **SC 1.3.1 Info and Relationships (A)** — Headings, lists, tables, reading order must be programmatically determinable. For **untagged PDFs**, PDF.js extracts text and positions; reading order is "best-effort top-to-bottom, left-to-right" without semantic heading info. For **tagged PDFs** (PDF/UA), the struct-tree overlay maps `<span role="heading" aria-level="N">` over the text layer and uses `aria-owns` to bind structural-tree nodes to their text spans.
- **SC 2.1.1 Keyboard (A)** — All viewer functionality (page next/prev, zoom, page input, search) operable via keyboard. The text layer is keyboard-navigable for selection (Tab + arrow keys + browser shortcuts).
- **SC 4.1.2 Name, Role, Value (A)** — Every viewer control (toolbar buttons, page input, search field, page list) needs accessible name, role, value. This is the gap G-PV5/G-PV6/G-PV7 already calls out — the toolbar's button glyphs need `aria-label`s.

### 4.2 Level AA (additional)

- **SC 1.4.3 Contrast (Minimum) (AA)** — Toolbar UI contrast 4.5:1 (text) / 3:1 (UI components). The PDF content itself is content-author territory — out of scope for the viewer to enforce, but the viewer must not *reduce* contrast by overlaying it.
- **SC 1.4.5 Images of Text (AA)** — Where a PDF is a scanned image with no embedded text (no text layer extractable), the viewer cannot fix this — it's a content-author problem. The viewer SHOULD detect the case and surface a warning (see §8).
- **SC 1.4.10 Reflow (AA)** — Content reflowable at 400% zoom without horizontal scrolling. PDF.js's `currentScaleValue: 'page-fit' | 'page-width' | 'auto'` modes address this. The current iframe approach delegates to the browser's PDF UI, which honors zoom but reflow at 400% is browser-dependent.
- **SC 2.4.6 Headings and Labels (AA)** — Headings/labels describe topic/purpose. For PDF content this requires tagged source PDFs. For viewer UI it requires good `aria-label`s on the toolbar.
- **SC 2.4.7 Focus Visible (AA)** — Keyboard focus indicator visible. Standard React/Tailwind focus-ring discipline covers this.

### 4.3 Beyond WCAG: PDF/UA (ISO 14289)

PDF/UA is the document-side accessibility standard. A WCAG-conformant viewer + a PDF/UA-conformant source PDF together produce an end-to-end accessible experience. **Harborline generates the PDFs it shows (invoices, statements, leases) — so we control the source side and can ensure they are tagged.** This is a substantive lever: if we make the PDFs we *render* tagged, the PDF.js struct-tree overlay carries that semantic info, and Harborline achieves WCAG AA + PDF/UA end-to-end. ONR flags this as a follow-up question for the PDF-generation pipeline (likely `PDFExport.*`).

---

## 5. Recommended architecture

### 5.1 DOM structure (the load-bearing pattern)

The canonical accessible-PDF-viewer DOM, as PDF.js implements it and as `react-pdf` exposes:

```html
<section role="region" aria-label="Invoice INV-2026-06-06-0001-0001" class="pdf-viewer">
  <!-- Toolbar landmark -->
  <div role="toolbar" aria-label="PDF controls" class="pdf-toolbar">
    <button aria-label="Previous page">‹</button>
    <span aria-live="polite" aria-atomic="true">Page 1 of 4</span>
    <button aria-label="Next page">›</button>
    <button aria-label="Zoom out">−</button>
    <span>100%</span>
    <button aria-label="Zoom in">+</button>
    <a aria-label="Open PDF in new tab" href="..." target="_blank">Open</a>
  </div>

  <!-- Document landmark -->
  <div role="document" aria-label="Invoice content" class="pdf-document">

    <!-- One page (multiplied per loaded page) -->
    <div class="pdf-page" data-page-number="1">
      <!-- (a) Visual layer — canvas, hidden from AT -->
      <canvas aria-hidden="true" width="..." height="..."></canvas>

      <!-- (b) Text layer — invisible, AT-readable, position-aligned over canvas -->
      <div class="textLayer" role="presentation">
        <span style="left: 12px; top: 40px; ...">Invoice</span>
        <span style="left: 12px; top: 72px; ...">Number: INV-2026-06-06-0001-0001</span>
        <span style="left: 12px; top: 104px; ...">Date: 2026-06-06</span>
        <!-- ... one span per text chunk PDF.js extracts ... -->
      </div>

      <!-- (c) Annotation layer — interactive elements (links, form fields) -->
      <div class="annotationLayer">
        <a href="#page=2" aria-label="Internal link to page 2">View payment schedule</a>
        <!-- form fields, etc -->
      </div>

      <!-- (d) Struct tree layer — semantic ARIA overlay for tagged PDFs (optional) -->
      <div class="structTreeLayer" aria-hidden="false">
        <span role="heading" aria-level="1" aria-owns="text-span-id-3">Invoice</span>
        <!-- ... per tag in the PDF's struct tree ... -->
      </div>
    </div>
  </div>
</section>
```

### 5.2 Why each layer matters for WCAG

| Layer | WCAG criteria addressed |
| --- | --- |
| Canvas (visual) — `aria-hidden="true"` | Hidden from AT so the text layer is the canonical content path (avoids double-reading). |
| Text layer — invisible, position-aligned spans | SC 1.1.1 (text alternative for the canvas), SC 1.3.1 (reading order), SC 1.4.5 (real text, not images of text) |
| Annotation layer — real `<a>`, `<input>`, etc. | SC 2.1.1 (keyboard-interactive elements use native semantics), SC 4.1.2 (name/role/value via native HTML) |
| Struct tree layer (when source is tagged) | SC 1.3.1 (heading hierarchy + table semantics), SC 2.4.6 (headings/labels) |
| Toolbar with `aria-label`s + `aria-live` page indicator | SC 4.1.2 (toolbar buttons), SC 4.1.3 status messages (page change announcement) |
| Outer `role="region"` + `role="document"` landmarks | SC 1.3.1 / SC 2.4.1 Bypass Blocks (lets AT users jump in/out of the PDF region) |

### 5.3 Layer responsibilities in `react-pdf`

`react-pdf` exposes these layers as props on `<Page>`:

- `renderTextLayer={true}` (default) — emits the text-layer spans
- `renderAnnotationLayer={true}` (default) — emits the annotation-layer
- Custom `customTextRenderer` callback — lets us inject our own ARIA wrappers
- Struct-tree rendering is **not yet fully wired in `react-pdf`** ([issue #1494](https://github.com/wojtekmaj/react-pdf/issues/1494) closed without merge); for v1 we accept this gap and document it. The text-layer + annotation-layer alone already moves us from G-PV4 FAIL to substantially compliant for untagged PDFs.

---

## 6. Recommended OSS dependency

### 6.1 Pick: `react-pdf` v10.4.1 (MIT) + `pdfjs-dist` v5.4.296 (Apache-2.0)

**Concrete install:**

```bash
pnpm add react-pdf@^10.4.1
# pdfjs-dist is a transitive dep of react-pdf — pinned to 5.4.296 by react-pdf's package.json
```

**Peer-dep posture** (verified against `react-pdf` v10.4.1 `package.json`):

- `react` `^16.8.0 || ^17.0.0 || ^18.0.0 || ^19.0.0` — covers our React 18/19 fleet posture
- `react-dom` same range
- `@types/react` same range (optional)

### 6.2 Rationale

1. **MIT-licensed** (verified in `package.json` and README). Compatible with the fleet's "MIT OSS only" posture.
2. **Built on `pdfjs-dist` (Apache-2.0)** — the canonical Mozilla PDF.js distribution. Apache-2.0 is compatible with MIT for downstream consumers.
3. **Active maintenance** — last release 2026-02-25; ~950K weekly npm downloads; Wojciech Maj is a known, long-running maintainer.
4. **React 19 ready** — peer deps declare `^19.0.0`. We don't have a downgrade trap on the React side.
5. **Battery-included AT pipeline** — text layer and annotation layer rendering are first-class; we don't have to build them ourselves on top of bare `pdfjs-dist`.
6. **API surface fits our `PDFViewerProps`** — the existing `data | url`, `page | defaultPage | onPageChange`, `zoom`, `height | width`, `toolbar`, `className` props map naturally to `react-pdf`'s `<Document>` + `<Page>` props. We can keep our public TypeScript surface unchanged.
7. **Bundle cost is acceptable when dynamic-imported.** See §5.4 below.

### 6.3 Why not `pdfjs-dist` directly

We could skip `react-pdf` and use `pdfjs-dist` directly (it's the engine `react-pdf` wraps anyway). The trade-off:

- **Pro of direct:** smaller dependency surface, no wrapper-version-skew risk, full access to `PDFViewer` / `EventBus` / `PDFFindController` / `PDFLinkService` for advanced features (search, highlights, custom annotations).
- **Con of direct:** we have to write the React lifecycle wiring ourselves (cancel-on-unmount, page-render scheduling, scale management, worker config, CMap config, standard-font config). This is roughly 200–400 lines of subtle code that `react-pdf` already maintains.

For v1, **adopt `react-pdf`**. If we hit a hard wall (a feature it doesn't expose), we can drop down to direct `pdfjs-dist` in a later wave without changing the `PDFViewerProps` surface.

### 6.4 Why not the alternatives

- **`@react-pdf-viewer/core` (Phuoc Nguyen):** Commercial license. Fleet posture is MIT-OSS. Hard exclude.
- **`@embedpdf/react-pdf-viewer`:** MIT, but uses a custom PDFium-WASM engine (not PDF.js). Two concerns: (a) AT story not documented in marketing; (b) PDFium-via-WASM is a young pipeline relative to PDF.js's 14+ years of production use. Re-evaluate in a year.
- **`@react-pdf-kit/viewer`:** MIT and uses `pdfjs-dist`, but lower download counts and smaller community than `react-pdf`. Less battle-tested. Hold as fallback.
- **Roll-our-own on bare `pdfjs-dist`:** valid but expensive (see §6.3). Defer.

### 6.5 Bundle-size trade-offs and mitigations

`pdfjs-dist` is heavy:

- Library: ~840 KB raw / ~500 KB min+gzip
- Worker: ~700 KB raw / ~250 KB min+gzip (uses the `.min.js` build, roughly half the dev worker)

That's ~750 KB gz added to a bundle that ships `<PDFViewer>` eagerly. **Mitigations (apply all three):**

1. **Dynamic import** — `PDFViewer.tsx` does `const ReactPdf = await import('react-pdf')` inside `useEffect`. The bundle splits cleanly; bundle-graph cost is 0 KB for routes that don't render a `<PDFViewer>`.
2. **Worker as a same-origin static asset** — host `pdf.worker.min.js` under our own `/public/pdf-worker/` (or equivalent) instead of via `unpkg`/`cdnjs`. Cache-friendly; survives CDN outages; avoids COOP/COEP cross-origin worker traps.
3. **CMap + standard fonts as on-demand fetches** — `pdfjs-dist` loads CJK CMaps and standard fonts on-demand from `cMapUrl` and `standardFontDataUrl`. Configure these to point at same-origin static directories. Most documents (e.g., latin-only invoices) never trigger these fetches.

The fleet already ships heavier vendor bundles (e.g., the kendo-react grid family). The marginal cost is acceptable for the AT-correctness gain.

---

## 7. Migration path from the current iframe implementation

### 7.1 Preserve the `PDFViewerProps` public surface

The current props (`data`, `url`, `page`, `defaultPage`, `onPageChange`, `zoom`, `height`, `width`, `toolbar`, `className`) all map cleanly to a `react-pdf`-backed implementation. **No breaking change to consumers.**

### 7.2 Phased implementation

**Phase 1 — Build the canvas+text-layer renderer (replaces iframe):**

```tsx
// PDFViewer.tsx (sketch — illustrative, not final code)
import * as React from 'react'
import { Document, Page, pdfjs } from 'react-pdf'
import 'react-pdf/dist/Page/TextLayer.css'
import 'react-pdf/dist/Page/AnnotationLayer.css'

// One-time worker config (place in a module-level init file)
pdfjs.GlobalWorkerOptions.workerSrc = '/pdf-worker/pdf.worker.min.js'

export function PDFViewer(props: PDFViewerProps) {
  // ... existing state for currentPage, currentZoom ...
  const [numPages, setNumPages] = React.useState<number | null>(null)

  const file = React.useMemo(() => props.url ?? props.data, [props.url, props.data])

  return (
    <section
      role="region"
      aria-label="PDF document"
      className={cn('flex flex-col border border-border rounded-md overflow-hidden', props.className)}
      style={{ width: props.width, height: typeof props.height === 'number' ? `${props.height}px` : props.height }}
    >
      {props.toolbar !== false && (
        <div role="toolbar" aria-label="PDF controls" className="...">
          <button type="button" aria-label="Previous page" onClick={...} disabled={currentPage <= 1}>‹</button>
          <span aria-live="polite" aria-atomic="true" className="text-xs">
            Page {currentPage}{numPages ? ` of ${numPages}` : ''}
          </span>
          <button type="button" aria-label="Next page" onClick={...} disabled={!!numPages && currentPage >= numPages}>›</button>
          {/* zoom buttons with aria-label, Open link with aria-label */}
        </div>
      )}
      <div role="document" aria-label="PDF content" className="flex-1 overflow-auto">
        <Document
          file={file}
          onLoadSuccess={({ numPages }) => setNumPages(numPages)}
          loading={<div role="status">Loading PDF…</div>}
          error={<div role="alert">Failed to load PDF</div>}
          noData={<div>No PDF source provided</div>}
        >
          <Page
            pageNumber={currentPage}
            scale={currentZoom}
            renderTextLayer
            renderAnnotationLayer
          />
        </Document>
      </div>
    </section>
  )
}
```

This phase alone resolves G-PV4 (text layer carries the content to AT), G-PV5 (toolbar `aria-label`s), G-PV6 (`aria-live` on the page indicator), G-PV7 (Open link gets an `aria-label`). The Accessibility contract can be updated to mark these resolved.

**Phase 2 — Dynamic-import the heavy chunk:**

Wrap the `react-pdf` import in `React.lazy` + `Suspense`, or use a dynamic-import inside `useEffect`. Either way, `pdfjs-dist` only loads on first `<PDFViewer>` mount.

**Phase 3 (optional, follow-up wave) — Tagged-PDF struct-tree overlay:**

Hook `customTextRenderer` to call `pdfjs-dist`'s `getTextContent({ includeMarkedContent: true })` and emit `role="heading" aria-level="N"` spans with `aria-owns` pointing at the text-layer span IDs. This depends on Harborline's PDF-generation pipeline producing tagged PDFs (PDF/UA). File this as a separate ADR amendment; do not block Phase 1 on it.

### 7.3 Test plan (resolves the M1 acceptance gates)

- **Axe / pa11y scan** — `<section role="region" aria-label>` + `<div role="document">` + toolbar buttons with `aria-label`s should pass axe rule `aria-required-attr`, `button-name`, `region`.
- **VoiceOver smoke** — load a known invoice PDF; confirm VoiceOver reads the heading + body text from the text layer (rotor "Text" navigation should reach the content).
- **NVDA smoke** — same on Windows; confirm reading-order matches visual order.
- **Keyboard smoke** — Tab into the region, Tab through toolbar, Shift-Tab out, arrow-key page changes (if we wire arrow-key handlers).
- **Bundle-size budget check** — confirm the `<PDFViewer>` chunk is dynamic-imported and the route's initial bundle is unchanged.

### 7.4 Rollback posture

The iframe implementation can stay in `git log` as a known-good fallback. If `react-pdf` causes a regression (worker config snag, CSP friction, scale glitch), the rollback is a one-commit revert — `PDFViewerProps` is unchanged so consumers don't shift.

---

## 8. Known limitations (carry-forward gaps to document)

The text-layer architecture is a substantial improvement over the iframe approach, but it does not solve every PDF-accessibility problem. Be explicit about what we are *not* solving in this migration:

| Limitation | Explanation | Mitigation |
| --- | --- | --- |
| **PDFs with no text layer (scanned-image PDFs)** | If the source PDF is a scanned image (no embedded text), `getTextContent()` returns empty. The text layer is blank. AT users see "blank document." | Detect at load time (text content empty across all pages) → emit a visible + screen-reader-announced warning: "This PDF appears to be an image. Text is not available to assistive technology." Long-term: server-side OCR pipeline (out of scope here). |
| **Password-protected PDFs** | `pdfjs-dist` supports password callbacks but the React component needs a UI for the prompt. The current iframe defers to the browser, which has its own UI; `react-pdf` requires us to build one. | Phase 1 punts: documents are unencrypted Harborline-generated PDFs. If we later support user-uploaded PDFs that may be encrypted, file a separate ADR for the password prompt UI. |
| **Untagged PDFs lose heading semantics** | Without source-side PDF/UA tags, the text layer reads as a flat run of text — no headings, no list semantics, no table relationships. SC 1.3.1 is partially failed (reading order yes; structure no). | Harborline controls the source PDF pipeline. File a follow-up directive against `PDFExport.*` to emit PDF/UA-tagged PDFs. With tagged source + Phase 3 struct-tree overlay, full SC 1.3.1 compliance is reachable. |
| **Large documents (100+ pages)** | Loading all pages at once exhausts memory; rendering one page at a time (as the current toolbar pattern does) is fine, but AT users navigating by next-heading cannot cross page boundaries. | Out of scope for v1. The `react-pdf` `<Document>` supports rendering N pages in a virtualized list — file a separate enhancement if/when consumers need it. |
| **Tables in PDFs** | Even with PDF/UA tags, PDF.js's struct-tree overlay support for tables is partial. Complex tables may not have full row/column header binding. | Document as a known gap. Harborline's invoice tables are simple (single row header); workable. |
| **`react-pdf` struct-tree gap** | Issue [#1494](https://github.com/wojtekmaj/react-pdf/issues/1494) — full `includeMarkedContent` + struct-tree rendering not yet first-class in `react-pdf` (the underlying `pdfjs-dist` capability exists). | Phase 3 — implement via `customTextRenderer`. Acceptable to defer. |
| **PDF.js's own viewer accessibility issues** | [mozilla/pdf.js#20114](https://github.com/mozilla/pdf.js/issues/20114) (2025-07-22, unresolved) tracks heading-role / table-role / toolbar ARIA gaps in the *bundled PDF.js viewer UI*. We're NOT using that bundled viewer — we're using PDF.js as a rendering engine inside our own React shell — so most of these gaps don't apply to us (we control our toolbar). | Track upstream for awareness; no action. |
| **VoiceOver `aria-owns` limited support** | The struct-tree pattern relies on `aria-owns` to bind structural spans to text-layer spans; macOS VoiceOver historically has uneven `aria-owns` support. | Phase 3 risk. Mitigate by mirroring the text content into the structural span (`aria-label`) as a fallback, or by inlining text directly inside the structural span. |

---

## 9. References

### Primary sources

- **WCAG 2.1 (W3C Recommendation)** — [https://www.w3.org/TR/WCAG21/](https://www.w3.org/TR/WCAG21/). Cited criteria: SC 1.1.1 Non-text Content (A), SC 1.3.1 Info and Relationships (A), SC 1.4.5 Images of Text (AA), SC 1.4.10 Reflow (AA), SC 2.1.1 Keyboard (A), SC 2.4.6 Headings and Labels (AA), SC 2.4.7 Focus Visible (AA), SC 4.1.2 Name, Role, Value (A). Retrieved 2026-06-06.
- **Understanding SC 1.1.1 Non-text Content** — [https://www.w3.org/WAI/WCAG21/Understanding/non-text-content.html](https://www.w3.org/WAI/WCAG21/Understanding/non-text-content.html). Retrieved 2026-06-06.
- **`react-pdf` package.json** — [https://github.com/wojtekmaj/react-pdf/blob/main/packages/react-pdf/package.json](https://github.com/wojtekmaj/react-pdf/blob/main/packages/react-pdf/package.json). License MIT; v10.4.1; `pdfjs-dist` 5.4.296; React peer deps `^16.8.0 || ^17.0.0 || ^18.0.0 || ^19.0.0`. Retrieved 2026-06-06.
- **`react-pdf` repository** — [https://github.com/wojtekmaj/react-pdf](https://github.com/wojtekmaj/react-pdf). MIT, text-layer + annotation-layer rendering. Retrieved 2026-06-06.
- **`react-pdf` releases** — [https://github.com/wojtekmaj/react-pdf/releases](https://github.com/wojtekmaj/react-pdf/releases). v10.4.1 released 2026-02-25; v10.0.0 introduced ESM-only build. Retrieved 2026-06-06.
- **`pdfjs-dist` on npm** — [https://www.npmjs.com/package/pdfjs-dist](https://www.npmjs.com/package/pdfjs-dist). Apache-2.0; latest v6.0.227 (May 2026), v5.x is the line `react-pdf` v10.4.1 currently pins. Retrieved 2026-06-06.
- **PDF.js GitHub** — [https://github.com/mozilla/pdf.js](https://github.com/mozilla/pdf.js). Mozilla's canonical PDF.js implementation.
- **PDF.js Accessibility Issue #20114** — [https://github.com/mozilla/pdf.js/issues/20114](https://github.com/mozilla/pdf.js/issues/20114). Open 2025-07-22; tracks bundled viewer-UI ARIA gaps. Retrieved 2026-06-06.
- **`react-pdf` Issue #1494 — Match accessibility features offered by pdfjs viewer** — [https://github.com/wojtekmaj/react-pdf/issues/1494](https://github.com/wojtekmaj/react-pdf/issues/1494). May 2023; closed without merge; documents the struct-tree gap. Retrieved 2026-06-06.
- **React PDF Viewer License (Phuoc Nguyen)** — [https://react-pdf-viewer.dev/license/](https://react-pdf-viewer.dev/license/). Commercial license required for production use. Retrieved 2026-06-06.
- **EmbedPDF** — [https://www.embedpdf.com/react-pdf-viewer](https://www.embedpdf.com/react-pdf-viewer). MIT, custom PDFium-WASM engine. Retrieved 2026-06-06.

### Secondary sources

- **"Implementing form filling and accessibility in the Firefox PDF viewer"** — Mozilla Hacks, 2021. [https://hacks.mozilla.org/2021/10/implementing-form-filling-and-accessibility-in-the-firefox-pdf-viewer/](https://hacks.mozilla.org/2021/10/implementing-form-filling-and-accessibility-in-the-firefox-pdf-viewer/). Describes the canvas + text layer + struct-tree-overlay pattern with `aria-owns` / `role="heading"` / `aria-level`. Retrieved 2026-06-06.
- **"Understanding PDF.js Layers and How to Use them in React.js"** — [https://www.react-pdf-kit.dev/blog/understanding-pdfjs-layers-and-how-to-use-them-in-reactjs](https://www.react-pdf-kit.dev/blog/understanding-pdfjs-layers-and-how-to-use-them-in-reactjs). Describes the 4-layer architecture (canvas, text, annotation, structural). Retrieved 2026-06-06.
- **WebAIM WCAG 2 Checklist** — [https://webaim.org/standards/wcag/checklist](https://webaim.org/standards/wcag/checklist). Cross-reference for which criteria apply to embedded content. Retrieved 2026-06-06.
- **iFrame Accessibility Reference Guide (UMass Dartmouth)** — [https://www.umassd.edu/accessibility/digital-accessibility/iframes/](https://www.umassd.edu/accessibility/digital-accessibility/iframes/). Confirms iframe `title` describes the frame, not its contents. Retrieved 2026-06-06.

### Tertiary / context

- **DAISY Consortium — Accessible PDF guidance** — [https://daisy.org/guidance/info-help/guidance-training/content-creation/accessible-pdf/](https://daisy.org/guidance/info-help/guidance-training/content-creation/accessible-pdf/). PDF/UA vs WCAG. Retrieved 2026-06-06.
- **"How to build a React PDF viewer with react-pdf" — Nutrient (formerly PSPDFKit) blog** — [https://www.nutrient.io/blog/how-to-build-a-reactjs-pdf-viewer-with-react-pdf/](https://www.nutrient.io/blog/how-to-build-a-reactjs-pdf-viewer-with-react-pdf/). Anecdotal — written by a competitor (commercial PDF viewer vendor) so flagged as biased toward their product, but useful for the API-shape walkthrough. Retrieved 2026-06-06.
