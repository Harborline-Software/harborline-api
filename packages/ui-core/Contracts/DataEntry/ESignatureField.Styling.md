# ESignatureField — Styling Contract

- **Component:** ESignatureField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ESignatureField.Semantic.md) · [Interaction](./ESignatureField.Interaction.md) · [Accessibility](./ESignatureField.Accessibility.md) · [Styling](./ESignatureField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ESignatureField.tsx` (not yet implemented)
- **Catalog row:** #A20 ESignatureField (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Overview

ESignatureField is a compound component. Its visual structure has six regions,
each with its own recipe:

1. Root container
2. Context banner (optional — shown when `documentTitle` is provided)
3. Method tab bar
4. Capture panel (draw / type / upload)
5. Signer identity section (optional)
6. Acceptance section
7. Complete state indicator

All recipes use Tailwind utility classes and the design system's semantic token
set. No hardcoded color hex values.

---

## 2. Root container

```
flex flex-col gap-4 w-full
rounded-lg border border-input bg-background p-4
```

Error state (when `error === true` or `FormFieldContext.error`):

```
border-destructive
```

Disabled state:

```
opacity-60 pointer-events-none
```

---

## 3. Context banner

Rendered only when `documentTitle` is provided. Positioned above the method
tabs.

```
flex items-center gap-2 px-3 py-2
rounded-md bg-muted text-sm text-muted-foreground
border border-border
```

Document title text:

```
font-medium text-foreground
```

Signer role badge (when `signerRole` is provided):

```
inline-flex items-center rounded-full px-2 py-0.5
bg-primary/10 text-primary text-xs font-medium
```

---

## 4. Method tab bar

### 4.1 Tab list container

```
flex border-b border-border gap-0
```

### 4.2 Individual tab button

Base (all tabs):

```
px-4 py-2 text-sm font-medium
text-muted-foreground
border-b-2 border-transparent
transition-colors
hover:text-foreground hover:border-border
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1
```

Active tab (aria-selected="true"):

```
text-foreground border-b-2 border-primary
```

Disabled tab (when `disabled === true` or `complete === true`):

```
opacity-50 cursor-not-allowed pointer-events-none
```

---

## 5. Capture panel

### 5.1 Panel container

```
min-h-[180px] pt-3
```

### 5.2 Draw panel — canvas area

The draw panel container (wrapping canvas + Clear button) **must have `relative` positioning** so the `absolute top-1 right-1` Clear button anchors to the canvas corner, not the nearest positioned ancestor:

```
relative
```

The canvas inherits from the `Signature` component's recipe:

```
w-full rounded-md border border-input cursor-crosshair
```

Disabled canvas:

```
cursor-not-allowed opacity-50
```

Instructions paragraph (visible or sr-only):

```
mt-1.5 text-xs text-muted-foreground
```

Clear button (draw method):

```
absolute top-1 right-1
px-1.5 py-0.5 rounded text-xs
bg-white/80 dark:bg-black/40
border border-border
text-muted-foreground hover:text-foreground
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
```

### 5.3 Type panel

Typed-name input:

```
w-full rounded-md border border-input bg-background px-3 py-2
text-sm text-foreground
placeholder:text-muted-foreground
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2
disabled:cursor-not-allowed disabled:opacity-50
```

Signature font preview container:

```
mt-3 flex items-center justify-center
w-full min-h-[80px] rounded-md border border-dashed border-border
bg-muted/30 px-4 py-3
```

Preview text (name rendered in signature font):

```
text-[2rem] leading-tight text-foreground
```

**Do NOT** use a Tailwind arbitrary class for the font-family (`font-[var(--esig-type-font,...)]`). Multi-word font names with quotes inside Tailwind arbitrary brackets frequently fail to compile in v3 (the bracket parser treats the comma as a separator). Apply font-family via inline `style` prop instead:

```jsx
<p
  className="text-[2rem] leading-tight text-foreground"
  style={{ fontFamily: "var(--esig-type-font, 'Dancing Script', 'Brush Script MT', cursive)" }}
>
  {typedName}
</p>
```

The `font-family` is set via an inline CSS variable set from
`typedFontFamily` prop: `style={{ '--esig-type-font': typedFontFamily }}`.

Empty preview placeholder:

```
text-sm text-muted-foreground italic
```

Text: "Your signature will appear here"

### 5.4 Upload panel

Drop zone (default / empty):

```
flex flex-col items-center justify-center gap-3
w-full rounded-lg border-2 border-dashed border-border
bg-muted/20 px-6 py-8
cursor-pointer
transition-colors
```

Drop zone (drag-active):

```
border-primary bg-primary/5
```

Drop zone instructions text:

```
text-sm text-muted-foreground text-center
```

Browse button:

```
inline-flex items-center gap-1.5 rounded-md
px-3 py-1.5 text-sm font-medium
bg-secondary text-secondary-foreground
hover:bg-secondary/80
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2
```

Upload zone with image (after successful upload):

```
flex flex-col items-center gap-2 w-full
rounded-lg border border-border bg-background p-3
```

Thumbnail image:

```
max-h-[160px] max-w-full rounded object-contain
```

Remove button:

```
text-xs text-muted-foreground underline underline-offset-2
hover:text-destructive
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
```

Validation error message (invalid file type):

```
mt-1.5 text-xs text-destructive flex items-center gap-1
```

---

## 6. Signer identity section

Section header:

```
text-xs font-medium text-muted-foreground uppercase tracking-wider mb-2
```

Text: "Signer information"

Field row (name / title / date):

```
flex flex-col gap-1
```

Field label:

```
text-sm font-medium text-foreground
```

Field input:

```
rounded-md border border-input bg-background px-3 py-1.5
text-sm text-foreground
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2
disabled:bg-muted disabled:cursor-not-allowed disabled:opacity-60
```

Read-only date field (when `signerDateEditable === false`):

```
bg-muted cursor-default
```

Required field error message:

```
text-xs text-destructive mt-0.5
```

The identity section's grid when multiple fields are visible simultaneously:

```
grid grid-cols-1 sm:grid-cols-2 gap-3
```

---

## 7. Acceptance section

Container:

```
flex flex-col gap-2 pt-2 border-t border-border
```

Checkbox row:

```
flex items-start gap-2
```

Checkbox (Radix primitive or native `<input type="checkbox">`):

```
mt-0.5 h-4 w-4 shrink-0 rounded
border border-input ring-offset-background
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2
data-[state=checked]:bg-primary data-[state=checked]:text-primary-foreground
disabled:cursor-not-allowed disabled:opacity-50
```

Acceptance label text:

```
text-sm text-foreground leading-snug
```

---

## 8. Complete state indicator

Shown below the acceptance section when `complete === true`.

Indicator banner:

```
flex items-center gap-2 rounded-md px-3 py-2
bg-green-50 dark:bg-green-950 border border-green-200 dark:border-green-800
text-green-800 dark:text-green-200 text-sm font-medium
```

Icon (checkmark — `lucide-react` `Check` at size 16):

```
text-green-600 dark:text-green-400 shrink-0
```

"Clear and re-sign" button:

```
mt-1 text-xs text-muted-foreground underline underline-offset-2
hover:text-foreground
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
```

---

## 9. Focus visible — global rule

All interactive elements within ESignatureField use:

```
focus-visible:outline-none
focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2
```

`focus:outline-none` alone (without the `focus-visible:ring-*` pair) is
PROHIBITED within this component. This resolves the known gap from
`Signature.Accessibility G2` that ESignatureField inherits in the draw panel.

---

## 10. Semantic token inventory

| Token | Used on |
|---|---|
| `border-input` | Root container, canvas, text inputs, upload zone |
| `border-border` | Context banner, tab bar, identity section divider |
| `border-destructive` | Root container (error state) |
| `border-primary` | Active tab underline |
| `bg-background` | Root container, text inputs |
| `bg-muted` | Context banner, read-only date field, upload zone |
| `bg-muted/20` | Upload drop zone base |
| `bg-primary/10` | Signer role badge background |
| `bg-secondary` | Browse button |
| `text-foreground` | Body text, active tab, labels |
| `text-muted-foreground` | Inactive tabs, instructions, field labels (secondary), clear button |
| `text-primary` | Signer role badge text |
| `text-destructive` | Validation errors, root border (error) |
| `text-secondary-foreground` | Browse button label |
| `ring-ring` | Focus ring (all focusable elements) |

---

## 11. Dark mode

All recipes use semantic tokens which resolve correctly in dark mode when the
host provides a `dark` class on a parent element (standard Tailwind dark mode).
No explicit `dark:` overrides are required except on the complete-state banner
(§7 uses `dark:bg-green-950`, `dark:border-green-800`, `dark:text-green-200`,
`dark:text-green-400`) where the green-50/green-200/green-800 tokens do not
have semantic-token equivalents in the current design system.

---

## 12. Responsive behaviour

The root container uses `w-full` — width is driven by the host's layout. All
inner elements use relative units (`w-full`, `max-w-full`, percentage gaps)
so the component adapts to any container width.

At narrow widths (container < ~360px), the method tab labels MAY be truncated.
The implementation SHOULD use `aria-label` on each tab button so that the AT
label remains descriptive even when the visible text is clipped.

Identity field grid collapses from two columns to one column on containers
narrower than the `sm` breakpoint (Tailwind default: 640px).

---

## 13. Animation / transition notes

| Element | Transition |
|---|---|
| Tab bar — inactive to active | `border-b-2` color fade: `transition-colors duration-150` |
| Upload drop zone — drag enter/leave | Border color + background fade: `transition-colors duration-100` |
| Complete state banner | Entrance: `animate-in fade-in-0 slide-in-from-bottom-1 duration-200` (if `tailwindcss-animate` is available) |

Transitions are decorative. All state changes must be immediately observable
without animation for users with `prefers-reduced-motion`. The implementation
SHOULD wrap transition utilities with:

```
motion-safe:transition-colors motion-safe:duration-150
```

rather than bare `transition-colors` to respect the OS-level reduced-motion
preference.
