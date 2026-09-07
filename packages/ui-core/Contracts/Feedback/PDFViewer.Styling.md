# PDFViewer — Styling Contract

- **Component:** PDFViewer
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFViewer.Semantic.md) · [Interaction](./PDFViewer.Interaction.md) · [Accessibility](./PDFViewer.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFViewer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root container

`flex flex-col border border-border rounded-md overflow-hidden` + `className` passthrough.

Dimensions set via inline `style` from `width` and `height` props.

---

## 2. Toolbar

`flex items-center gap-2 border-b border-border bg-muted/30 px-3 py-1.5 text-sm shrink-0`

Uses `border-border`, `bg-muted/30` design tokens.

### Toolbar buttons (page nav, zoom)

`rounded px-2 py-0.5 hover:bg-accent disabled:opacity-40`

### Page label

`text-xs text-muted-foreground`

### Zoom display

`text-xs text-muted-foreground w-12 text-center`

### Open link

`rounded px-2 py-0.5 hover:bg-accent text-xs`

---

## 3. iframe

`flex-1 w-full border-0`

Zoom applied via inline style: `transform: scale(currentZoom); transformOrigin: 'top left'`.
Width/height compensate for scale to avoid clipping:
`width: ${100 / currentZoom}%`, `height: ${100 / currentZoom}%`.

---

## 4. No-source placeholder

`flex-1 flex items-center justify-center text-sm text-muted-foreground`

Shown when no PDF source is available.

---

## 5. Design token dependency

`border-border`, `bg-muted/30`, `hover:bg-accent`, `text-muted-foreground` are
design tokens. These must be present in the consuming app's Tailwind config.
