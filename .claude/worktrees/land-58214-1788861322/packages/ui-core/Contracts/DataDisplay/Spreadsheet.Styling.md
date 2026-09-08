# Spreadsheet — Styling Contract

- **Component:** Spreadsheet
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Spreadsheet.Semantic.md) · [Interaction](./Spreadsheet.Interaction.md) · [Accessibility](./Spreadsheet.Accessibility.md) · [Styling](./Spreadsheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Spreadsheet.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root wrapper

```
flex flex-col border border-border rounded-md overflow-hidden font-mono text-sm
```

`font-mono` — monospace font throughout (formula bar, cells, headers).

## 2. Formula bar

`flex items-center gap-2 px-2 py-1 border-b border-border bg-muted/30 shrink-0`

Cell reference box: `w-16 text-center text-xs text-muted-foreground border border-border rounded px-1 py-0.5`

Value display: `flex-1 text-xs text-muted-foreground border border-border rounded px-2 py-0.5 bg-background min-h-[22px]`

## 3. Grid container

`overflow-auto flex-1` — scrollable; fills remaining height.

## 4. Table

`border-collapse` with `tableLayout: 'fixed'`.

## 5. Column headers (`<th>`)

Sticky header: `sticky top-0 z-10 bg-muted/30 border border-border px-1 text-center text-xs text-muted-foreground`

Corner cell (top-left): additionally `sticky left-0 z-20`

## 6. Row number cells (`<td>` first column)

`sticky left-0 bg-muted/30 border border-border px-1 text-center text-xs text-muted-foreground`

## 7. Data cells (`<td>`)

Base: `border border-border relative p-0 overflow-hidden`

Selected (not editing): `outline outline-2 outline-primary outline-offset-[-2px]`

Locked: `bg-muted/10`

Cell content span: `block px-1 truncate` with `lineHeight: ${rowHeight}px`

## 8. Edit input (inline)

`absolute inset-0 w-full h-full px-1 bg-background border-0 outline-none text-sm font-mono z-10`

## 9. CSS variables used

| Variable | Purpose |
|---|---|
| `--border` | Cell borders, formula bar border |
| `--muted` | Header and formula bar backgrounds (`bg-muted/30`) |
| `--muted-foreground` | Header text, reference box text |
| `--primary` | Selected cell outline colour |
| `--background` | Edit input background, value display background |
