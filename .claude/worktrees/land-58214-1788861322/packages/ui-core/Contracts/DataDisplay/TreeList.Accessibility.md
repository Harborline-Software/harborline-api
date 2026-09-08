# TreeList — Accessibility Contract

- **Component:** TreeList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TreeList.Semantic.md) · [Interaction](./TreeList.Interaction.md) · [Styling](./TreeList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/TreeList.tsx`
- **Catalog row:** #141 TreeList (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Table semantics

Uses native `<table>`, `<thead>`, `<tbody>`, `<tr>`, `<th>`, `<td>` — correct semantic structure. Screen readers announce it as a data table with column headers.

---

## 2. Expand button

`aria-label={isExpanded ? 'Collapse' : 'Expand'}` — updates dynamically with expand state. Button is type="button".

---

## 3. Tree hierarchy announcement

No `aria-level`, `aria-expanded` on rows, or `role="treegrid"` pattern. Screen readers receive no hierarchical context — depth is communicated only visually via indentation (G-TRLST1).

---

## 4. Hidden expand button

When a row has no children, the expand button has `visibility: hidden` (CSS) but remains in the DOM with `aria-label` — screen readers may still encounter it (G-TRLST2).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TRLST1 | High | No `role="treegrid"` / `aria-level` / `aria-expanded` on rows — AT users cannot perceive hierarchy | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-TRLST2 | Medium | Invisible expand buttons for leaf rows remain in DOM — AT may announce them unnecessarily | Accepted-risk M1; should use `aria-hidden` on leaf buttons |
| G-TRLST3 | Medium | No keyboard navigation between rows per ARIA treegrid pattern | Accepted-risk M1 |

---

## Full-surface expansion (2026-06-11)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope source:** `_shared/design/polish/kendo-spec-audit/tree-list-views.md` — TreeList
audit rows 1-4. G-TRLST1 is a Level-A blocker already noted above; all items in §FS-1 address
it. FR-2 rulings apply (see `_shared/design/polish/family-rulings-2026-06-11.md`).

---

### §FS-1 Wave — `role="treegrid"` + full ARIA pattern (closes G-TRLST1/2/3)

**G-TRLST1 is a WCAG SC 4.1.2 Level A violation.** The WAI-ARIA `treegrid` pattern is the
correct role for a table with hierarchical row relationships. All items below are required
before v1 ship.

#### §FS-1.1 Table role upgrade

Replace `<table>` with the `treegrid` ARIA pattern:

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `role="treegrid"` | Root `<table>` | Declares the widget as a treegrid | G-TRLST1 |
| `aria-label` or `aria-labelledby` | `<table>` | Caller-supplied label or a visible caption | G-TRLST1 |
| `aria-rowcount` | `<table>` | Total row count (visible + collapsed) when known | G-TRLST1 |
| `aria-colcount` | `<table>` | Number of columns | G-TRLST1 |
| `role="rowgroup"` | `<thead>`, `<tbody>` | Already implicit; make explicit | G-TRLST1 |

#### §FS-1.2 Row-level ARIA

| Attribute | Element | Value | Gap closed |
|---|---|---|---|
| `role="row"` | `<tr>` (already implicit on table rows; make explicit) | — | G-TRLST1 |
| `aria-level` | `<tr role="row">` | Depth + 1 (root nodes = 1, first-level children = 2, …) | G-TRLST1 |
| `aria-expanded` | `<tr role="row">` (branch nodes only) | `"true"` when expanded, `"false"` when collapsed; omit on leaf rows | G-TRLST1 |
| `aria-selected` | `<tr role="row">` | `"true"` / `"false"` when `selectionMode !== 'none'`; omit when `'none'` | audit row 4 |
| `aria-rowindex` | `<tr role="row">` | 1-based position in the full row set (visible rows only; matches visible order) | G-TRLST1 |
| `role="gridcell"` | `<td>` (data cells) | Replaces implicit cell role in treegrid context | G-TRLST1 |
| `role="columnheader"` | `<th>` | Already correct; confirm explicitly | G-TRLST1 |
| `aria-colindex` | `<td role="gridcell">` | 1-based column index | G-TRLST1 |

#### §FS-1.3 Leaf expand button (closes G-TRLST2)

Leaf-row expand buttons are currently `visibility: hidden` but remain in the DOM with an
`aria-label`. Fix: add `aria-hidden="true"` to the expand button element for leaf rows, OR
remove the button from the DOM for leaf rows (preferred — eliminates the dead tab stop).

Preferred DOM shape for leaf rows:
- No `<button>` element in the first `<td>`.
- Replace with a plain `<span aria-hidden="true" class="w-4 mr-1.5 inline-block" />` spacer
  (same visual indent; not interactive).

#### §FS-1.4 Sorting ARIA (wave-3 — audit row 2)

When `sortable === true`:

| Attribute | Element | Value |
|---|---|---|
| `role="columnheader button"` | Sortable `<th>` inner button | Native `<button>` is sufficient |
| `aria-sort` | Sortable `<th>` | `"ascending"` / `"descending"` / `"none"` |

Unsorted sortable headers: `aria-sort="none"`. No-sort (`sort` cleared): `aria-sort="none"`.

#### §FS-1.5 Selection ARIA (wave-3 — audit row 4)

When `selectionMode === 'multiple'`:

- `<table role="treegrid">` gains `aria-multiselectable="true"`.
- Each `<tr role="row">` gains `aria-selected="true"` / `"false"` (required for `treegrid`
  with selection; previously noted as omitted in G-TRLST1).
- The leading checkbox `<td role="gridcell">` contains `<input type="checkbox">`. The
  checkbox `aria-label` is `"Select row {rowId}"` to avoid bare "checkbox" announcement.
- Header tri-state checkbox: `aria-label="Select all rows"`, `aria-checked="true"` /
  `"false"` / `"mixed"` (indeterminate).

When `selectionMode === 'single'`:

- `<table role="treegrid">` does NOT gain `aria-multiselectable`.
- `<tr aria-selected="true" / "false">` is still required per treegrid pattern.

#### §FS-1.6 Keyboard navigation ARIA (wave-3 — closes G-TRLST3)

The treegrid pattern specifies two-dimensional focus management. Council question: adopt the
full two-dimensional grid model (cell-level focus with arrow keys) or a one-dimensional row-
focus model (arrow keys navigate between rows; Tab moves into cells within a row)?

**Interim row-focus model (wave-3, pending council ruling):**

- Each `<tr role="row">` has `tabIndex={0}` (active row) or `tabIndex={-1}`.
- Exactly one row has `tabIndex={0}` at a time (roving tabIndex across rows).
- ArrowDown/ArrowUp navigate between rows (move tabIndex + call focus()).
- Tab/Shift+Tab within a row navigates between interactive cells (expand button, editable
  cells when in edit mode).

**Full two-dimensional model (wave-5 target, pending council ruling):**

- `<td role="gridcell">` elements have `tabIndex={0}` / `tabIndex={-1}`.
- Arrow keys navigate cell-by-cell (including across rows).
- Home/End move to first/last cell in the current row.
- Ctrl+Home/End move to first/last cell in the grid.

Council gate added to tracking. The wave-3 row-focus interim model is the default until the
council confirms the two-dimensional model is required for the MVP accessibility bar.

#### §FS-1.7 Editing ARIA (wave-4 — audit row 1)

When a row or cell is in edit mode:

- The active edit input has `aria-label="{column.title}"` to avoid bare "text input"
  announcement.
- The row in edit mode has `aria-label="Editing {first-column-value} row"` on the `<tr>`.
- Validation errors are announced via `role="alert"` on the error message element below the
  cell, OR via `aria-describedby` linking the cell to the error element.
- The Save / Cancel buttons in row-edit mode are `type="button"` with visible labels.
