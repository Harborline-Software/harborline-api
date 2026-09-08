# FileManager — Styling Contract

- **Component:** FileManager
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FileManager.Semantic.md) · [Interaction](./FileManager.Interaction.md) · [Accessibility](./FileManager.Accessibility.md) · [Styling](./FileManager.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FileManager.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FileManager uses semantic color tokens from the design system (`border`,
`muted`, `accent`, `destructive`, `primary`) and does not use raw color
values. This contract names the structural layout recipes and visual state
classes.

---

## 2. Root container

```
flex flex-col border border-border rounded-md overflow-hidden
```

---

## 3. Toolbar

```
flex items-center gap-1.5 border-b border-border bg-muted/30 px-3 py-2
```

Toolbar buttons (text buttons):
```
text-xs px-2 py-1 rounded hover:bg-accent disabled:opacity-40
```

Delete button (destructive):
```
text-xs px-2 py-1 rounded hover:bg-accent text-destructive
```

---

## 4. List view

Table container: `w-full text-sm`

Header row: `border-b border-border bg-muted/20 text-muted-foreground text-xs`

Header cell: `text-left px-3 py-1.5 font-medium`

Data row:
```
border-b border-border last:border-0 cursor-pointer
```

Data row — selected: `bg-primary/10`
Data row — idle hover: `hover:bg-muted/40`

Name cell: `px-3 py-1.5 flex items-center gap-2`
Size/date cells: `px-3 py-1.5 text-right text-xs text-muted-foreground`

Empty folder row: `px-3 py-8 text-center text-sm text-muted-foreground`

---

## 5. Grid view

Grid container: `grid grid-cols-4 gap-3 p-3`

Grid cell:
```
flex flex-col items-center gap-1.5 rounded-md p-2 cursor-pointer text-center
```

Grid cell — selected: `bg-primary/10`
Grid cell — hover: `hover:bg-muted/40`

Grid item label: `text-xs truncate w-full text-center`

---

## 6. Rename input

```
border border-input rounded px-1 py-0.5 text-sm outline-none focus:ring-1 focus:ring-ring
```

---

## 7. Icons

- `FolderIcon`: `fill-amber-400` (small: `h-4 w-4 shrink-0`, large: `h-8 w-8`)
- `DocIcon`: `fill-muted-foreground` (same sizes)

---

## 8. Semantic color tokens consumed

| Token class | Semantic meaning |
|---|---|
| `border-border` | Component border |
| `bg-muted/30`, `bg-muted/20`, `bg-muted/40` | Subdued background fills |
| `hover:bg-accent` | Hover highlight |
| `text-muted-foreground` | Secondary text |
| `bg-primary/10` | Selected state background |
| `text-destructive` | Delete action text |
| `border-input` | Input border (rename field) |
| `focus:ring-ring` | Focus ring (rename field) |
