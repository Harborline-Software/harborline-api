# Spreadsheet — Interaction Contract

- **Component:** Spreadsheet
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Spreadsheet.Semantic.md) · [Interaction](./Spreadsheet.Interaction.md) · [Accessibility](./Spreadsheet.Accessibility.md) · [Styling](./Spreadsheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Spreadsheet.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Cell selection

| Trigger | Effect |
|---|---|
| Click a cell | Sets `selected` to that cell's ref; exits any active edit |

---

## 2. Edit entry

| Trigger | Effect |
|---|---|
| Double-click a cell | Calls `startEdit(ref)` — enters edit mode if cell is not locked |
| `Enter` key on selected cell | Calls `startEdit(ref)` — enters edit mode |
| `F2` key on selected cell | Calls `startEdit(ref)` — enters edit mode |

`startEdit` sets `editing = ref`, `editValue = getCellDisplay(ref)`, then focuses the inline `<input>` on the next animation frame.

Locked cells (`cell.locked === true`) skip edit entry — `startEdit` returns early.

---

## 3. Edit commit

| Trigger | Effect |
|---|---|
| `Enter` key in edit input | Calls `commitEdit(ref)` |
| `blur` on edit input | Calls `commitEdit(ref)` |

`commitEdit` saves the cell:
- If `editValue.startsWith('=')` → `{ formula: editValue, value: editValue }`
- Otherwise → `{ value: editValue }`

Updates internal state (uncontrolled), calls `onCellChange(ref, cell)`, clears `editing` and `editValue`.

---

## 4. Edit cancel

| Trigger | Effect |
|---|---|
| `Escape` key in edit input | Clears `editing` and `editValue`; reverts to previous cell content |

---

## 5. Cell deletion

| Trigger | Effect |
|---|---|
| `Delete` or `Backspace` on selected cell (not in edit mode) | Removes the cell from `data` (sets to `{}`); calls `onCellChange(ref, {})` |

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SPRI1 | High | No arrow-key navigation between cells — arrow keys unhandled; focus stays on current cell after Enter commit; standard spreadsheet navigation not implemented | Fix-deferred M2 — keyboard navigation is a fundamental spreadsheet interaction; implement in M2 |
| G-SPRI2 | High | No Tab-to-next-cell — Tab does not advance selection to next cell in row | Fix-deferred M2 — coordinate with G-SPRI1 keyboard-nav implementation |
| G-SPRI3 | Medium | No multi-cell selection — only single-cell; no range selection, copy, or paste | Accepted-risk M1 — complex feature; deferred past M1 |
| G-SPRI4 | Medium | No formula evaluation — `=`-prefixed strings stored as literals; no arithmetic evaluation | Accepted-risk M1 — formula engine is out of scope for M1 |
| G-SPRI5 | Medium | `SpreadsheetCell.format` prop not applied — defined in interface but not used in rendering or display logic | Fix-deferred M2 — wire `format` into the cell display pipeline |
| G-SPRI6 | Medium | Controlled mode state reversal — `onCellChange` fires but internal `data` not updated; cell display reverts until host updates prop; no "pending" indicator | Fix-deferred M2 — either fully uncontrolled-mimic (optimistic update) or document the flicker as a host responsibility; add pending-state spec |

---

## Full-surface expansion (2026-06-11 — waves 2-5, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

---

### §FS-1 Wave-2 — keyboard navigation (closes G-SPRI1 + G-SPRI2)

**Status:** Draft

#### §FS-1.1 Arrow-key cell navigation

After committing or cancelling an edit, focus returns to the selected cell container
(not the `<input>`). Arrow keys move selection:

| Key | Effect |
|---|---|
| ArrowRight | Select cell one column right; clamp at last column |
| ArrowLeft | Select cell one column left; clamp at column A |
| ArrowDown | Select cell one row down; clamp at last row |
| ArrowUp | Select cell one row up; clamp at row 1 |
| Enter | Move selection one row down (post-commit navigation, matching Excel default) |
| Tab | Move selection one column right; wrap to first column of next row at last column |
| Shift+Tab | Move selection one column left; wrap to last column of previous row at first column |
| Home | Move selection to column A of current row |
| End | Move selection to the last non-empty column of current row; falls back to the last rendered column |
| Ctrl+Home | Move selection to A1 |
| Ctrl+End | Move selection to the last non-empty cell (bottom-right extent of data) |

Navigation wraps sheet boundaries: ArrowDown on last row selects first row (or clamps —
council to decide; default: clamp).

**wave-2**

---

#### §FS-1.2 Edit-mode navigation

When a cell is in edit mode (`editing` is set):

- Enter commits the cell (existing §3) and moves selection one row down.
- Tab commits the cell and moves selection one column right (wrap at row end).
- Shift+Tab commits the cell and moves selection one column left.
- Escape cancels the edit (existing §4); selection stays on the current cell.
- Arrow keys while editing do NOT navigate — they move the cursor within the
  `<input>` text. Arrow navigation activates only in non-edit mode.

**wave-2**

---

### §FS-2 Wave-3 — multi-sheet tabs

**Status:** Draft

#### §FS-2.1 Props additions

```typescript
interface SheetDescriptor {
  id: string
  title: string
  data?: SpreadsheetData       // sheet-specific data (uncontrolled) or undefined for controlled
  defaultData?: SpreadsheetData
}

// Additions to SpreadsheetProps:
sheets?: SheetDescriptor[]       // when supplied, multi-sheet mode activates
activeSheet?: string             // controlled active sheet id
defaultActiveSheet?: string      // uncontrolled seed; defaults to sheets[0].id
onSheetChange?: (sheetId: string) => void
onSheetAdd?: () => void          // called when user clicks "+" to add a sheet
onSheetDelete?: (sheetId: string) => void
onSheetRename?: (sheetId: string, newTitle: string) => void
```

When `sheets` is absent, single-sheet mode (current behavior) is unchanged.

#### §FS-2.2 Tab strip behavior

A tab strip renders below the toolbar (or at the bottom of the component, matching
Excel convention). Each tab shows `sheet.title`.

- Clicking a tab fires `onSheetChange(sheetId)` and switches the active sheet.
- The active tab is visually elevated (border-bottom removed; background-elevated token).
- Double-clicking a tab label puts it into inline rename mode: a text input replaces
  the label. Pressing Enter or blurring commits; fires `onSheetRename(id, newTitle)`.
  Pressing Escape cancels and reverts to the original title.
- "+" button at the end of the tab strip calls `onSheetAdd`. The parent is responsible
  for appending a new `SheetDescriptor` and switching to it.
- Right-click (context menu) on a tab offers "Rename" and "Delete" options.
  "Delete" calls `onSheetDelete(sheetId)`. Deleting the last sheet is disallowed
  (the menu item is disabled).

#### §FS-2.3 Per-sheet cell state

Each sheet maintains independent `selected`, `editing`, and `data` state.
Switching sheets exits any active edit on the departing sheet (commits, does not cancel —
matching Excel behavior). The formula bar updates to show the newly-active cell on the
arrived sheet.

**wave-3**

---

### §FS-3 Wave-3 — freeze panes

**Status:** Draft

#### §FS-3.1 Props

```typescript
// Additions to SpreadsheetProps:
frozenRows?: number      // rows 1..frozenRows are sticky (default: 0)
frozenColumns?: number   // columns 1..frozenColumns are sticky (default: 0)
```

#### §FS-3.2 Behavior

Frozen rows render with `position: sticky; top: 0` inside the scroll container.
Frozen columns render with `position: sticky; left: <computed-offset>`.
The row/column header panel always stays visible regardless of freezing settings
(headers are independently sticky).
Cell refs, selection, and keyboard navigation are not affected by freeze pane
configuration — `A1` is always `A1`.

**wave-3**

---

### §FS-4 Wave-3 — merge cells

**Status:** Draft

#### §FS-4.1 Merge descriptor

```typescript
interface CellMerge {
  ref: string          // top-left cell of the merged region (e.g., "B2")
  rowSpan: number      // rows to span (>= 1)
  colSpan: number      // columns to span (>= 1)
}

// Addition to SpreadsheetProps:
merges?: CellMerge[]
```

#### §FS-4.2 Behavior

A merged cell renders with `rowSpan` and `colSpan` set on the `<td>`.
Cells consumed by a merge are not rendered.
Click selection on a merged cell sets `selected` to the merge's `ref` (the top-left).
Keyboard navigation treats merged cells as a single cell of size `colSpan × rowSpan`;
ArrowRight from a merged cell skips to the column `ref.col + colSpan`.

Editing a merged cell is permitted; the value is stored on the `ref` cell.
The formula bar shows the merged cell's value.

Merges are read from the `merges` prop; the component does NOT provide a UI to create or
delete merges in wave-3 (programmatic-only).

**wave-3**

---

### §FS-5 Wave-4 — cell formatting toolbar

**Status:** Draft

#### §FS-5.1 Toolbar anatomy

A formatting toolbar renders above the formula bar. Controls (in order):

| Control | Action | Cell property set |
|---|---|---|
| Bold toggle | `Ctrl+B` shortcut too | `style.fontWeight: 'bold' / 'normal'` |
| Italic toggle | `Ctrl+I` shortcut | `style.fontStyle: 'italic' / 'normal'` |
| Underline toggle | `Ctrl+U` shortcut | `style.textDecoration: 'underline / none'` |
| Font family selector | Dropdown of available fonts | `style.fontFamily` |
| Font size input | Integer pt value | `style.fontSize` (px, converted from pt: `px = pt * 96 / 72`) |
| Text color swatch | Color picker popup | `style.color` |
| Fill (background) color swatch | Color picker popup | `style.backgroundColor` |
| Border selector | Dropdown (none/all/outer/bottom) | `borders` property — see §FS-5.2 |
| Horizontal align buttons | Left / Center / Right | `style.textAlign` |

All toolbar actions apply to the currently-selected cell. When no cell is selected,
toolbar controls are disabled.

Toolbar fires `onCellChange(ref, updatedCell)` after each formatting change, following
the same controlled/uncontrolled semantics as inline edits.

FR-2 applies: the toolbar slot (`toolbar?: React.ReactNode`) allows a host to replace
or augment the default formatting toolbar.

**wave-4**

---

#### §FS-5.2 Cell borders (CellBorder interface)

```typescript
interface CellBorder {
  top?: { color?: string; width?: number; style?: 'solid' | 'dashed' | 'dotted' }
  right?: { color?: string; width?: number; style?: 'solid' | 'dashed' | 'dotted' }
  bottom?: { color?: string; width?: number; style?: 'solid' | 'dashed' | 'dotted' }
  left?: { color?: string; width?: number; style?: 'solid' | 'dashed' | 'dotted' }
}

// Addition to SpreadsheetCell:
borders?: CellBorder
```

Cell borders are applied via CSS on each `<td>`. Adjacent-cell border conflicts (two
cells specifying opposite borders) are resolved by CSS natural cascade (both properties
set on both cells; last-write wins at the `<td>` level). A formal collapse algorithm
is a later-wave enhancement.

**wave-4**

---

### §FS-6 Wave-4 — import/export hooks

**Status:** Draft

#### §FS-6.1 Props

```typescript
// Additions to SpreadsheetProps:
onExcelImport?: (file: File) => Promise<SpreadsheetData | SheetDescriptor[]>
onExcelExport?: () => void    // host triggers export; Spreadsheet calls back when ready
toolbar?: React.ReactNode     // slot to add import/export buttons (FR-2 toolbar slot)
```

#### §FS-6.2 Import flow

The host renders an import button (via the `toolbar` slot) that opens a `<input type="file" accept=".xlsx,.csv">`.
On file selection, the host calls a parsing utility (not built into Spreadsheet — the
component boundary stops at the hook; parsing is host or utility territory) and passes
the result to `SpreadsheetProps.data` / `compositeValue` via the normal controlled-data
update path.

`onExcelImport` is an optional convenience hook: when supplied, Spreadsheet renders a
default "Import" button in the toolbar that opens the file dialog and calls
`onExcelImport(file)`. The returned `SpreadsheetData` replaces the current sheet data
(or `SheetDescriptor[]` replaces the entire sheets array in multi-sheet mode).

**NOTE — formula engine boundary:** imported `.xlsx` files may contain formula strings.
Spreadsheet stores them as `cell.formula` and displays as-is; it does NOT evaluate them.
This is a deliberate boundary (see §FS-7 formula engine note).

#### §FS-6.3 Export flow

`onExcelExport` is called when the user activates export. The hook receives the current
`SpreadsheetData` as its argument (signature: `onExcelExport(data: SpreadsheetData) => void`).
The host is responsible for serializing and triggering the browser download.
Spreadsheet does NOT bundle a serialization library.

**wave-4**

---

### §FS-7 Wave-5+ — formula evaluation engine

**Status:** Draft — LATER WAVE; v1 slice only specced here.

#### §FS-7.1 Scope decision

A full formula evaluation engine (support for `=SUM`, `=IF`, `=VLOOKUP`, cell
cross-references, date serial numbers, user-defined functions) is a substantial
sub-system with significant bundle-size implications (existing OSS: HyperFormula ~350KB
minified). It is explicitly deferred past v1 (wave-5+).

**v1 slice (wave-5):** support the five arithmetic operators and seven basic functions:
`SUM`, `AVERAGE`, `MIN`, `MAX`, `COUNT`, `IF`, `ROUND`. Cell references (`A1`, `A1:A5`)
within the same sheet. No cross-sheet references, no user-defined functions, no
date serials.

#### §FS-7.2 Props for v1 slice

```typescript
// Addition to SpreadsheetProps:
formulaEngine?: 'none' | 'basic'    // default: 'none' (existing behavior)
```

When `formulaEngine === 'basic'`, cells whose `formula` starts with `=` are evaluated
on each render. The evaluated result is displayed in the cell; the formula is shown in
the formula bar (same as existing behavior for the formula-bar display side).
Circular references render `#CIRC!`; parse errors render `#ERR!`.

The formula engine is bundled as a lazy-loaded chunk (dynamic `import()`) to avoid
bundle-size impact when `formulaEngine` is `'none'`.

**wave-5**

---

#### §FS-7.3 Cell-change event in formula mode

When a formula cell's dependencies change (a referenced cell is edited), the formula
cell re-evaluates and fires `onCellChange(ref, { formula: cell.formula, value: newEvalResult })`
so the host's controlled data stays consistent with the evaluated value.

**wave-5**
