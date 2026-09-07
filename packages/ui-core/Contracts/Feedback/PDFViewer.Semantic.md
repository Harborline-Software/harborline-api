# PDFViewer — Semantic Contract

- **Component:** PDFViewer
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PDFViewer.Interaction.md) · [Accessibility](./PDFViewer.Accessibility.md) · [Styling](./PDFViewer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFViewer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<iframe>` or PDF.js viewer

---

## 1. Purpose

PDFViewer renders a **browser-native PDF display panel** via an `<iframe>`
pointing at a PDF URL or Blob. It provides an optional toolbar for page
navigation and zoom control. Supports both remote URLs and in-memory PDF
data (ArrayBuffer, Uint8Array).

---

## 2. Data model

```typescript
interface PDFViewerProps {
  data?: ArrayBuffer | Uint8Array | string
  url?: string
  page?: number
  defaultPage?: number
  onPageChange?: (page: number) => void
  zoom?: number
  height?: string | number
  width?: string | number
  toolbar?: boolean
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `data` | `ArrayBuffer \| Uint8Array \| string` | — | In-memory PDF data. Converted to a Blob URL. Mutually exclusive with `url` — `url` takes precedence. |
| `url` | `string` | — | A URL pointing to a PDF file. |
| `page` | `number` | — | Controlled current page. When provided, overrides internal state. |
| `defaultPage` | `number` | `1` | Initial page on mount when `page` is not controlled. |
| `onPageChange` | `(page: number) => void` | — | Called when the user navigates pages via toolbar. |
| `zoom` | `number` | `1` | Initial zoom level (1 = 100%). Toolbar allows in-range adjustment. |
| `height` | `string \| number` | `'800px'` | Height of the viewer container. Numbers are treated as px. |
| `width` | `string \| number` | `'100%'` | Width of the viewer container. |
| `toolbar` | `boolean` | `true` | When true, renders the page navigation and zoom toolbar. |
| `className` | `string` | — | Additional classes on the root container. |

### 3.1 PDF source

Priority: `url` > `data`. When both are provided, `url` is used.

`data` as string is passed directly as a URL (not converted to Blob). This
allows data URIs but requires the string to be a valid URL-like value.

Blob URLs created from `data` are revoked on unmount (or when `objectUrl`
changes) to avoid memory leaks.

### 3.2 Page navigation

The `<iframe> src` is set to `${objectUrl}#page=${currentPage}&zoom=${Math.round(currentZoom * 100)}`.
This relies on the browser's built-in PDF viewer supporting hash parameters —
behavior varies by browser.

When `page` prop is provided (controlled), `useEffect` syncs internal state:
`if (page !== undefined) setCurrentPage(page)`.

### 3.3 Zoom range

Toolbar zoom uses `Math.max(0.25, z - 0.25)` (min 25%) and
`Math.min(4, z + 0.25)` (max 400%), stepped by 0.25.

The iframe uses `transform: scale(currentZoom)` which scales the entire
iframe — this is a visual-only zoom, not the PDF viewer's native zoom.

### 3.4 "Open" link

When `objectUrl` is available, the toolbar renders an `<a>` with
`target="_blank"` linking to the raw URL for download/external viewing.

---

## 4. Events

| Event | Signature | Trigger |
| --- | --- | --- |
| `onPageChange` | `(page: number) => void` | User clicks Previous or Next page button in toolbar. |

---

## 5. Composition

### View an invoice PDF from API response

```tsx
const { data } = useInvoicePDF(invoiceId)  // returns ArrayBuffer

<PDFViewer data={data} height="600px" />
```

### View a remote PDF URL

```tsx
<PDFViewer url="https://cdn.example.com/lease-agreement.pdf" defaultPage={3} />
```

---

## 6. Deferred features

- **Total page count** — no `totalPages` prop; toolbar shows current page
  only (cannot display "Page N of M").
- **Full-screen mode** — no toggle.
- **Text selection / search** — dependent on browser PDF viewer; not
  controlled by this component.
- **Print button** — not in the toolbar.
