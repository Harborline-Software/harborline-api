# PDFExport — Semantic Contract

- **Component:** PDFExport
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PDFExport.Interaction.md) · [Accessibility](./PDFExport.Accessibility.md) · [Styling](./PDFExport.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFExport.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — programmatic PDF generation (no UI primitive)

---

## 1. Purpose

PDFExport is a **transparent wrapper component** that enables its `children`
content to be exported to a PDF file via an imperative `ref.save()` call. It
exposes no visible UI of its own — the host provides a trigger (e.g., a "Download
PDF" button) and calls `ref.current.save()` to initiate the export.

An accompanying `usePDFExport` hook simplifies the ref management pattern.

---

## 2. Data model

```typescript
interface PDFExportRef {
  save: (fileName?: string) => void
}

interface PDFExportProps {
  children: React.ReactNode
  fileName?: string
  paperSize?: 'A4' | 'A3' | 'Letter' | 'Legal'
  landscape?: boolean
  margin?: {
    top?: string
    bottom?: string
    left?: string
    right?: string
  }
}

// Hook companion
function usePDFExport(fileName?: string): {
  ref: React.RefObject<PDFExportRef>
  save: () => void
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `children` | `ReactNode` | _required_ | The content to capture and export as PDF. Wrapped in a `<div>` that `exportToPDF` targets. |
| `fileName` | `string` | `'document.pdf'` | Default filename for the exported PDF. Can be overridden at call time via `ref.save(name)`. |
| `paperSize` | `'A4' \| 'A3' \| 'Letter' \| 'Legal'` | `'A4'` | Paper size for the PDF output. |
| `landscape` | `boolean` | `false` | When true, the PDF is landscape orientation. |
| `margin` | `{top?, bottom?, left?, right?}` | — | Page margin strings (CSS length values, e.g., `'10mm'`). |

### 3.1 `PDFExportRef.save(fileName?)`

Calling `ref.current.save()` captures the current DOM content of the
wrapper `<div>` and passes it to `exportToPDF` (from `../../lib/documentProcessing`).
An optional `fileName` argument overrides the prop-level default.

### 3.2 `usePDFExport` hook

```tsx
const { ref, save } = usePDFExport('invoice-2024-001.pdf')

<PDFExport ref={ref} paperSize="Letter">
  <InvoiceDocument />
</PDFExport>
<Button onClick={save}>Download PDF</Button>
```

---

## 4. Events

No events fired by PDFExport. Export is imperative (`ref.save()`).

---

## 5. Composition

### Invoice PDF download

```tsx
const { ref, save } = usePDFExport(`invoice-${invoice.number}.pdf`)

<PDFExport ref={ref} paperSize="Letter" landscape={false}>
  <InvoicePrintView invoice={invoice} />
</PDFExport>
<Button onClick={save}>Download PDF</Button>
```

---

## 6. Deferred features

- **Export progress / loading state** — no built-in loading indicator
  while `exportToPDF` processes.
- **Multiple export formats** — PDF only; no XLSX, PNG, etc.
- **Print trigger** — no built-in `window.print()` path; for browser
  print, use the host's print logic directly.
