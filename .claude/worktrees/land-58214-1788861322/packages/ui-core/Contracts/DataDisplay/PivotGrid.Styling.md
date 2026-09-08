# PivotGrid — Styling Contract

- **Component:** PivotGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PivotGrid.Semantic.md) · [Interaction](./PivotGrid.Interaction.md) · [Accessibility](./PivotGrid.Accessibility.md) · [Styling](./PivotGrid.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/PivotGrid.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scroll container

```
overflow-auto
```

---

## 2. Table

```
text-sm border-collapse w-full
```

---

## 3. Header row

```
bg-muted/30
```

Header cells (`<th>`):

```
border border-border px-3 py-1.5 text-left font-medium          (row dimension header)
border border-border px-3 py-1.5 text-center font-medium whitespace-nowrap  (column value headers)
border border-border px-3 py-1.5 text-center font-medium bg-muted/60         (grand total column header)
```

---

## 4. Data rows

Even rows: `bg-background`
Odd rows: `bg-muted/10`

Row label cell:

```
border border-border px-3 py-1.5 font-medium
```

Data cells:

```
border border-border px-3 py-1.5 text-right tabular-nums
```

Row grand total cell:

```
border border-border px-3 py-1.5 text-right font-medium bg-muted/30 tabular-nums
```

---

## 5. Grand total row

```
bg-muted/40 font-medium
```

Grand total row label cell:

```
border border-border px-3 py-1.5
```

Grand total data cells:

```
border border-border px-3 py-1.5 text-right tabular-nums
```

Grand total corner cell:

```
border border-border px-3 py-1.5 text-right tabular-nums
```

---

## 6. Token usage

PivotGrid uses semantic CSS custom property aliases from the design token system:

| Tailwind utility | Token alias | Notes |
|---|---|---|
| `bg-background` | `--background` | Even row background |
| `bg-muted/10` | `--muted` at 10% opacity | Odd row tint |
| `bg-muted/30` | `--muted` at 30% opacity | Header tint |
| `bg-muted/40` | `--muted` at 40% opacity | Grand total row |
| `bg-muted/60` | `--muted` at 60% opacity | Grand total column header |
| `border-border` | `--border` | Cell borders |
| `text-muted-foreground` | `--muted-foreground` | Empty-state text |

These tokens automatically adapt to dark mode if the provider theme supplies dark-mode token values.

---

## 7. Tabular numbers

Data cells and totals use `tabular-nums` to ensure digit alignment in columns. This is applied directly on data cells — no host action required.

---

## 8. Empty state

```
flex items-center justify-center p-8 text-sm text-muted-foreground border border-border rounded-md
```

---

## 9. Responsive behaviour

PivotGrid uses horizontal scroll (`overflow-auto`) to handle tables wider than the container. No responsive column stacking — the tabular structure is preserved at all viewports.
