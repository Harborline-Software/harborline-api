# TreeList — Styling Contract

- **Component:** TreeList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TreeList.Semantic.md) · [Interaction](./TreeList.Interaction.md) · [Accessibility](./TreeList.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/TreeList.tsx`
- **Catalog row:** #141 TreeList (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`overflow-auto rounded-md border border-border` + `className` passthrough.

---

## 2. Table

`w-full text-sm`

---

## 3. Header row

`<thead>`: `bg-muted/50`

`<th>`: `px-3 py-2 text-left font-semibold text-foreground whitespace-nowrap`

`style={{ width: col.width }}` applied when `col.width` is set.

---

## 4. Body rows

`<tr>`: `border-t border-border hover:bg-muted/30`

`<td>`: `px-3 py-2`

First column `<td>`: overrides padding via inline `style={{ paddingLeft: \`\${12 + depth * 20}px\`` }}` for depth-based indent.

---

## 5. Expand button

`inline-flex items-center justify-center w-4 h-4 mr-1.5 text-[10px] text-muted-foreground`

Leaf rows: adds `invisible` (visibility:hidden; layout space preserved).

---

## 6. Design tokens

Uses design tokens throughout: `border-border`, `bg-muted/50`, `text-foreground`, `bg-muted/30`, `text-muted-foreground`. No hardcoded palette colors.

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeList
styling surface gaps (audit rows 2, 3, 4, 7, 5). FR-3 rulings apply (see
`_shared/design/polish/family-rulings-2026-06-11.md`). The M1 §6 design-token baseline is
already clean; expansion sections add new visual surfaces only.

---

### §FS-1 Wave — sort + filter + selection visuals (audit rows 2, 3, 4)

#### §FS-1.1 Sort indicator styling (wave-3 — audit row 2)

Column header `<th>` changes when `sortable === true`:

```
<th> base: cursor-pointer select-none hover:bg-muted/70 transition-colors
     + existing: px-3 py-2 text-left font-semibold text-foreground whitespace-nowrap
```

Sort-indicator icon (inside the `<th>`):

| State | Class / token |
|---|---|
| Sortable, not active | `text-muted-foreground opacity-50` (neutral ⇅ icon) |
| Sorted asc | `text-foreground` (↑ ChevronUp icon) |
| Sorted desc | `text-foreground` (↓ ChevronDown icon) |

Focus ring on header button: `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring`

**wave-3**

---

#### §FS-1.2 Filter row styling (wave-3 — audit row 3)

When `filterable === true`, a second `<tr>` is rendered inside `<thead>`:

```
Filter header row <tr>: bg-background border-t border-border
Filter cell <td>:        px-2 py-1
Filter <input>:          w-full rounded-sm border border-input bg-transparent px-2 py-1
                         text-sm placeholder:text-muted-foreground
                         focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring
```

Funnel operator button (when `columnFilters` is controlled):

```
ml-1 inline-flex h-5 w-5 items-center justify-center rounded text-muted-foreground
hover:text-foreground hover:bg-accent
```

**wave-3**

---

#### §FS-1.3 Row selection visuals (wave-3 — audit row 4)

| Selection state | `<tr>` classes added |
|---|---|
| `'single'` or `'multiple'` — selected | `bg-accent/50 text-accent-foreground` |
| `'multiple'` — leading checkbox `<td>` | `w-10 px-2` (accommodates 16px checkbox + padding) |
| Header tri-state checkbox `<th>` | `w-10 px-2` |

Leading checkbox input: `h-4 w-4 rounded border border-input accent-primary cursor-pointer`

**wave-3**

---

### §FS-2 Wave — editing visuals (audit row 1)

#### §FS-2.1 Cell editor (wave-4)

Inline cell editor input:

```
w-full rounded-sm border border-primary bg-background px-2 py-0.5 text-sm
focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring
```

Cell in edit mode `<td>`: `ring-1 ring-primary ring-inset`

Validation error message (below cell):

```
mt-0.5 text-xs text-destructive
```

Dirty/pending cell indicator (batch mode):

```
border-l-2 border-l-warning pl-[10px]  (overrides default px-3 left-padding by 1px inset)
```

Save / Cancel buttons in row-edit mode:

```
Save:   inline-flex h-6 px-2 text-xs rounded-sm bg-primary text-primary-foreground
        hover:bg-primary/90
Cancel: inline-flex h-6 px-2 text-xs rounded-sm border border-input bg-background
        hover:bg-accent text-foreground ml-1
```

**wave-4**

---

### §FS-3 Wave — locked columns + aggregates (audit rows 7, 5)

#### §FS-3.1 Locked column styling (wave-5 — audit row 7)

Locked `<th>` and `<td>` cells:

```
position: sticky;
left: <cumulative-offset>px;  /* computed per column based on preceding locked column widths */
z-index: 1;                   /* above scrolling cells */
background: hsl(var(--background)); /* opaque background to hide underlying scrolling content */
```

A shadow separator between the last locked column and the first scrolling column:

```
/* Applied to the <td> / <th> of the last locked column: */
box-shadow: 2px 0 4px -2px hsl(var(--border));
```

**wave-5**

---

#### §FS-3.2 Aggregate footer row styling (wave-5 — audit row 5)

```
<tfoot>: border-t-2 border-border bg-muted/50
<td> in tfoot: px-3 py-2 text-sm font-semibold text-foreground
```

Aggregate label prefix (e.g. "Total:"):

```
text-muted-foreground font-normal mr-1
```

The tfoot row shares the same locked-column sticky treatment as tbody/thead when
`locked` columns are present (see §FS-3.1).

**wave-5**

---

#### §FS-3.3 Pager footer styling (wave-5 — audit row 8)

The built-in Pager component is rendered below the table inside the TreeList root wrapper
`<div>`. No additional TreeList-specific Pager styling is required — the Pager's own Styling
contract governs its appearance. The TreeList wrapper adds:

```
border-t border-border   /* on the div wrapping the Pager, separating it from the table */
```

**wave-5**
