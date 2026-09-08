# Upload — Styling Contract

- **Component:** Upload
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Upload.Semantic.md) · [Interaction](./Upload.Interaction.md) · [Accessibility](./Upload.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Upload.tsx`
- **Catalog row:** #144 Upload (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex flex-col gap-3` + `className` passthrough.

---

## 2. Button row

`flex items-center gap-2`

---

## 3. "Select files" button

`inline-flex h-9 items-center gap-1.5 rounded-md border border-input bg-background px-3 text-sm font-medium hover:bg-accent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring`

---

## 4. "Upload" button

`inline-flex h-9 items-center gap-1.5 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground hover:bg-primary/90 transition-colors disabled:pointer-events-none disabled:opacity-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring`

---

## 5. File list

`divide-y divide-border rounded-md border border-border`

Each item: `flex items-center gap-3 px-3 py-2 text-sm`

Filename: `flex-1 truncate`

File size: `text-xs text-muted-foreground`

---

## 6. Status indicators

Progress bar container: `w-20 h-1.5 rounded-full bg-secondary overflow-hidden`

Progress fill: `h-full bg-primary transition-all` with `style={{ width: ${progress}% }}`

Error text: `text-xs text-destructive`

Success text: `text-xs text-success-foreground` <!-- (M2: define `--success` design token; `text-emerald-600` is a hardcoded non-semantic color that breaks dark mode.) -->

---

## 7. Icons

Upload icon: `h-3.5 w-3.5 fill-current` (SVG)

X (remove) icon: `h-3.5 w-3.5 fill-current`

File icon: `h-4 w-4 fill-current shrink-0` — `text-success-foreground` (success), `text-destructive` (error), `text-muted-foreground` (default) <!-- (M2: define `--success` design token) -->

Remove button: `text-muted-foreground hover:text-foreground transition-colors`
