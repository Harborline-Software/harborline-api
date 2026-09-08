# PDFEditor — Styling Contract

- **Component:** PDFEditor
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PDFEditor.Semantic.md) · [Interaction](./PDFEditor.Interaction.md) · [Accessibility](./PDFEditor.Accessibility.md) · [Styling](./PDFEditor.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/PDFEditor.tsx` (not yet implemented)
- **Catalog row:** #A19 PDFEditor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Root container

```
flex flex-col border border-border rounded-md overflow-hidden
```

Plus `className` passthrough on the outermost element.

Dimensions are set via inline `style` from `width` and `height` props.
Numbers are coerced to `px`. Defaults: width `'100%'`, height `'800px'`.

---

## 2. Toolbar

The editor toolbar is a single strip at the top of the container, divided
into three logical groups separated by `<div role="separator" aria-hidden="true" class="...">`.

```
flex items-center gap-1 border-b border-border bg-muted/30 px-3 py-1.5 text-sm shrink-0 flex-wrap
```

`flex-wrap` allows the toolbar to reflow gracefully on narrow widths without
clipping.

### 2.1 Toolbar group separator

```
w-px h-4 bg-border mx-1
```

Used between: (a) tool selector group and page-nav group; (b) page-nav group
and Export button.

### 2.2 Tool-selector buttons

Each tool button is a compact icon button. States:

| State | Classes |
|---|---|
| Default (inactive) | `rounded p-1.5 text-muted-foreground hover:bg-accent hover:text-accent-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none transition-colors` |
| Active (`aria-pressed="true"`) | `rounded p-1.5 bg-accent text-accent-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none` |
| Disabled (`aria-disabled="true"`) | `rounded p-1.5 text-muted-foreground/40 cursor-not-allowed` |

Tool button icons are 16×16 (`size-4`). Icon-only buttons require a
visually-hidden label span for AT (see Accessibility contract §3.2):

```tsx
<button type="button" aria-label="Highlight text" aria-pressed={...} ...>
  <HighlightIcon className="size-4" aria-hidden="true" />
</button>
```

### 2.3 Page navigation controls

| Element | Classes |
|---|---|
| Previous / Next buttons | `rounded px-2 py-0.5 text-sm hover:bg-accent disabled:opacity-40 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none` |
| Page number input | `w-10 text-center text-xs rounded border border-input bg-background px-1 py-0.5 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none` |
| "of N" label | `text-xs text-muted-foreground` |

### 2.4 Zoom controls

| Element | Classes |
|---|---|
| Zoom out / Zoom in buttons | `rounded px-2 py-0.5 text-sm hover:bg-accent disabled:opacity-40 focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none` |
| Zoom level display | `text-xs text-muted-foreground w-14 text-center` |

### 2.5 Export button

Default state:

```
rounded px-3 py-1 text-xs font-medium bg-primary text-primary-foreground hover:bg-primary/90
focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
transition-colors ml-auto
```

`ml-auto` pushes Export to the trailing end of the toolbar row.

Loading state (while export is in progress):

```
... opacity-70 cursor-wait
```

Add a spinner icon (`animate-spin size-3 mr-1`) inline before the button
label text while `aria-busy="true"`.

---

## 3. Document / page area

```
flex-1 overflow-auto bg-muted/10 relative
```

`relative` establishes the stacking context for the annotation overlay and
signature zone overlay layers, which are positioned absolutely within this
container.

The `react-pdf` `<Document>` and `<Page>` render inside this wrapper.
The pdfjs text-layer and annotation-layer CSS is imported alongside the
component:

```ts
import 'react-pdf/dist/Page/TextLayer.css'
import 'react-pdf/dist/Page/AnnotationLayer.css'
```

The `<Page>` element has no additional wrapper classes; its own inline-style
dimensions are set by pdfjs based on scale.

Page centering within the scroll area:

```
flex flex-col items-center py-4 gap-4
```

Applied to the inner `<div>` that wraps the `<Page>` component(s).

---

## 4. Annotation overlay layer

The annotation overlay sits above the pdfjs canvas and text layer but
beneath the toolbar (z-layer managed by component stacking context):

```
absolute inset-0 pointer-events-none
```

`pointer-events-none` on the container means overlays only capture events
explicitly (`pointer-events-auto` on individual annotation divs when the
active tool is `read` for hover / click interaction).

### 4.1 Highlight annotation

```
absolute pointer-events-auto cursor-pointer
bg-yellow-300/40 hover:bg-yellow-300/60
ring-0 hover:ring-2 hover:ring-yellow-400
focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
rounded-sm transition-colors
```

Custom color variant via inline style `backgroundColor` + `borderColor`
(when the annotation `color` field is set; default is yellow).

Selected state:

```
ring-2 ring-primary bg-yellow-300/60
```

### 4.2 Underline annotation

Rendered as an absolutely-positioned thin bar at the bottom of the rect,
not a full-rect background:

```
absolute pointer-events-auto cursor-pointer h-0.5
bg-yellow-500 hover:bg-yellow-600
focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
```

The `height` of the element is `2px` (`h-0.5`); the `top` is set to align
with the text baseline (bottom of the rect).

Selected state:

```
bg-primary
```

### 4.3 Sticky-note anchor

The anchor icon that marks the note placement:

```
absolute pointer-events-auto cursor-pointer flex items-center justify-center
w-5 h-5 rounded-sm bg-amber-400 text-white
hover:bg-amber-500
focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
shadow-sm
```

Size is fixed at `20×20px` regardless of zoom; the anchor is positioned at
the top-left corner of the annotation rect.

### 4.4 Sticky-note popover

The popover floats near the sticky-note anchor. Use floating-ui or Radix
Popover for positioning. Visual styling:

```
w-64 rounded-md border border-border bg-popover shadow-md p-3 flex flex-col gap-2 z-50
```

Textarea within the popover:

```
w-full rounded border border-input bg-background text-sm px-2 py-1.5
resize-none min-h-[80px]
focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
```

Action row (Commit / Delete):

```
flex items-center justify-end gap-2
```

Commit button:

```
rounded px-2.5 py-1 text-xs font-medium bg-primary text-primary-foreground hover:bg-primary/90
focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
```

Delete button:

```
rounded px-2.5 py-1 text-xs font-medium text-destructive hover:bg-destructive/10
focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
```

---

## 5. Signature zone overlay

The signature zone is an absolutely-positioned container over the page
canvas at the coordinates derived from `signatureZone.rect`:

```
absolute rounded border-2 border-dashed border-primary/60 bg-background/80 overflow-hidden
```

When the Sign tool is active (interactive mode):

```
absolute rounded border-2 border-primary bg-background/90 shadow-md overflow-hidden
```

When non-empty preview mode:

```
absolute rounded border border-border bg-transparent overflow-hidden
```

The embedded Signature component fills the zone container `100%` width and
height (no internal margin). The Signature component's `className` prop
receives `'w-full h-full'`.

When the current page does not match `signatureZone.page`, the signature
zone overlay is not rendered (display:none or conditional).

---

## 6. Load states

### Loading

```
flex-1 flex items-center justify-center text-sm text-muted-foreground gap-2
```

Spinner icon: `animate-spin size-4`.

### Error

```
flex-1 flex flex-col items-center justify-center gap-3 text-sm text-destructive p-6
```

Retry button:

```
rounded px-3 py-1.5 text-xs font-medium bg-destructive/10 hover:bg-destructive/20
text-destructive focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none
```

### No source

```
flex-1 flex items-center justify-center text-sm text-muted-foreground
```

---

## 7. Export status notice

Rendered below the toolbar (or as a transient toast) after export completes:

Success:

```
text-xs text-green-600 flex items-center gap-1
```

Error:

```
text-xs text-destructive flex items-center gap-1
```

Both include a small icon (`size-3`) before the message text.

---

## 8. Design token dependencies

All tokens must be present in the consuming app's Tailwind config:

| Token | Usage |
|---|---|
| `border-border` | Container borders, toolbar border, separator, sticky-note popover |
| `bg-muted/30` | Toolbar background |
| `bg-muted/10` | Document area background |
| `hover:bg-accent` | Tool button and page-nav button hover |
| `text-accent-foreground` | Active tool button text |
| `text-muted-foreground` | Zoom display, page label, inactive tool buttons |
| `bg-primary` | Active tool button background, export button background, sign-active zone border |
| `text-primary-foreground` | Export button text |
| `ring-ring` | Focus rings |
| `border-input` | Page number input border |
| `bg-background` | Page number input background, signature zone background |
| `bg-popover` | Sticky-note popover background |
| `text-destructive` | Error state text, delete button |

`bg-yellow-300/40`, `bg-yellow-300/60`, `bg-yellow-500`, `bg-amber-400`,
`bg-amber-500`, `bg-green-600` are Tailwind palette colors (not custom
tokens). These are used only for annotation overlays and sticky-note icons
where semantic design-token equivalents do not exist.

---

## 9. Responsive / layout notes

- The toolbar uses `flex-wrap` so controls reflow on widths below ~480px.
- On very narrow widths (< 360px), consider hiding the zoom display span
  via `hidden sm:inline` while keeping the zoom buttons.
- The signature zone overlay scales proportionally with the PDF page zoom;
  its pixel dimensions are recalculated by the component on each zoom change.
- Annotation overlays are absolutely positioned in the same coordinate space
  as the pdfjs page canvas; they also scale with zoom. The implementation
  must re-derive pixel rects from PDF user-space rects on each zoom change.
