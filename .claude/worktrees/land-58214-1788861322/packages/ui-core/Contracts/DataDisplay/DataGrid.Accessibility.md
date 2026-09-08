# DataGrid — Accessibility Contract

- **Component:** DataGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DataGrid.Semantic.md) · [Interaction](./DataGrid.Interaction.md) · [Styling](./DataGrid.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/DataGrid.tsx`
- **Catalog row:** #35 DataGrid (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

DataGrid's accessibility contract has two jobs in M1:

1. **Pin the shipping baseline.** The React implementation today emits
   `aria-sort`, `aria-label`, and `aria-live` in specific places. This
   contract documents those emissions so they don't regress.
2. **Name the M0 → M1 deltas honestly.** DataGrid narrows or defers several
   DataGrid §1–§11 requirements (the WAI-ARIA `grid` role, full
   two-dimensional keyboard navigation, `aria-selected` on every selectable
   row). This contract records each delta + the deferred-feature linkage to
   the Semantic / Interaction contracts.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA 1.2
authoring practice.

---

## 2. ARIA structural roles

| Element | Current M1 emission | M0 baseline | Status |
|---|---|---|---|
| Outer container | `<div>` (no role) | container has `role="grid"` | **Deferred to M2** — DataGrid today renders the data inside a native `<table>` without the `grid` role, treating the structure as the "table" pattern with focusable interactive controls inside. This is a known regression from the M0 contract; the React-track council MUST decide whether to add `role="grid"` + the full keyboard model, or to stay on the `table` pattern with inline-control focus. |
| `<table>` | implicit `table` role | n/a (M0 uses `grid`) | Native table semantics provide row/column structure to AT. |
| `<thead>` | implicit `rowgroup` | n/a | — |
| `<tr>` (header) | implicit `row` | `row` | — |
| `<th>` | implicit `columnheader`; emits `aria-sort` | `columnheader` + `aria-sort` | **Parity** — see §3. |
| `<tbody>` | implicit `rowgroup` | n/a | — |
| `<tr>` (data) | implicit `row` (no `aria-selected`) | `row` with `aria-selected` when selectable | **Gap** — `aria-selected` is currently NOT emitted on data rows; selection is conveyed via row checkbox state + visual highlight only. See §4. |
| `<td>` | implicit `cell` | `gridcell` | — |
| Bulk-actions toolbar | `<div role="toolbar" aria-label="Bulk actions">` | not in M0 spec | **M1 addition**, see §6. |

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships — structural
information MUST be programmatically determined. The native `<table>` /
`<th>` / `<td>` elements satisfy this for the "table" reading pattern.

**Council open question.** Whether to migrate to the `role="grid"` pattern in a
follow-on PR is tracked under Semantic §3.3 + the gap entry above. The current
contract documents the shipping state; it does not amend DataGrid §1.

---

## 3. Sort

The shipping implementation emits `aria-sort` on column headers per the M0
DataGrid §2 rule:

| `aria-sort` value | Condition |
|---|---|
| `"ascending"` | `column.getIsSorted() === 'asc'` |
| `"descending"` | `column.getIsSorted() === 'desc'` |
| `"none"` | column is sortable but not currently the active sort column |
| _attribute omitted_ | column has `enableSorting: false` (e.g., the synthetic `__select__` column) |

At most one column at a time emits `"ascending"` or `"descending"` (single-sort
baseline; see Interaction §2).

The sort button inside the header is a native `<button type="button">` —
keyboard activation (Space / Enter) and focus management are inherited from
the platform.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Row selection

### 4.1 Selection-state programmatic exposure

The shipping implementation does **not** emit `aria-selected` on data rows in
M1. Selection state is conveyed to AT through three mechanisms:

1. The per-row checkbox (`<input type="checkbox" checked={...}>`) — checkbox
   state is announced by AT directly via its native semantics.
2. The bulk-actions toolbar's count text (`"{count} selected"`) is wrapped in
   `aria-live="polite"` — selection-count changes are announced.
3. The visual `bg-blue-50` row treatment — not programmatic, but cross-channel
   redundancy per WCAG 2.2 SC 1.4.1.

**This is a documented gap against DataGrid §3.** A follow-on PR SHOULD
emit `aria-selected="true"` on selected rows + `aria-selected="false"` on
unselected rows whenever `selectionEnabled`. The contract names the gap; the
implementation lands the fix in a separate change.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value — the current checkbox-
state channel satisfies the criterion at the row level, but row-level
`aria-selected` would make the selection state available to row-level AT
queries (e.g. JAWS "list selected items").

### 4.2 Checkbox accessible names

| Checkbox | `aria-label` (M1 baseline) |
|---|---|
| Header (select-all) | `"Select all"` |
| Per-row | `` `Select row ${row.id}` `` |

**Council open question.** The per-row label uses the row's internal id
(`row.id`, which is either `getRowId(row, index)` or the row index). Hosts that
supply a richer row identifier (e.g., a property name) likely want the
checkbox label to read `"Select 123 Main St"` rather than
`"Select row 1"`. The contract today documents the implementation; a follow-on
amendment SHOULD add a `getRowSelectionLabel?: (row, index) => string` prop to
Semantic §3 so hosts can supply the natural-language label.

### 4.3 Header tri-state checkbox

The header checkbox emits:

- `checked` when `table.getIsAllRowsSelected()` is true.
- `indeterminate` (set imperatively via the ref callback) when
  `table.getIsSomeRowsSelected()` is true and `getIsAllRowsSelected()` is
  false.

AT announces `indeterminate` as "mixed" for checkboxes per ARIA convention.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value (the indeterminate
state is programmatically determinable).

---

## 5. Keyboard navigation

DataGrid in M1 does **not** implement the WAI-ARIA `grid` two-dimensional
keyboard pattern. Keyboard navigation follows the native tab-order behaviour
of the underlying `<table>`:

| Key | Behaviour (M1 baseline) |
|---|---|
| Tab / Shift+Tab | Moves focus between **interactive controls** in document order: header sort buttons (left → right), bulk-actions toolbar (when visible), row checkboxes (top → bottom), then any cell-embedded interactive controls. Cells themselves are NOT focusable. |
| Enter / Space (on sort button) | Toggles the column's sort (the engine's `unsorted → asc → desc → unsorted` cycle). |
| Space (on row checkbox) | Toggles that row's selection. |
| Space (on header checkbox) | Toggles "select all on page". |
| Arrow keys | **Not bound by DataGrid.** Browser default behaviour applies (no grid-cell focus motion). |
| Home / End / PageUp / PageDown / Ctrl+Home / Ctrl+End | **Not bound by DataGrid.** |
| Escape | **Not bound by DataGrid.** |

This is a deliberate M1 simplification documented in Interaction §7 ("M1
DataGrid implementation does not wire grid-pattern keyboard navigation"). The
M0 DataGrid §4 full keyboard model is the target end-state; the React-
track adopts it in a follow-on cohort.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard — all functionality MUST be
keyboard operable. The shipping baseline satisfies this for **sort**,
**selection**, and **toolbar actions** because each interactive control is a
native focusable element (`<button>`, `<input type="checkbox">`). It does
NOT yet satisfy the "navigate freely between cells" expectation; that
expectation is deferred (the M1 deviation is principled because cells contain
no interactive content in the typical reverse-spec use case).

**Council open question.** Whether the React track adopts the full
`role="grid"` pattern + two-dimensional keyboard model in a follow-on PR, or
whether the M1 "table pattern with focusable controls" baseline stands as the
React-track interpretation. The contract documents the shipping state and
defers the architectural decision.

---

## 6. Bulk-actions toolbar

The toolbar is an M1-only surface (no M0 DataGrid analogue).

| Requirement | M1 emission |
|---|---|
| Container role | `role="toolbar"` |
| Container accessible name | `aria-label="Bulk actions"` |
| Count text | `<span aria-live="polite">{count} selected</span>` — selection-count changes are announced politely |
| "Clear" button | native `<button type="button">` — Space / Enter activation, focus ring inherited |

**Toolbar keyboard handling.** Per WAI-ARIA 1.2's toolbar pattern, a toolbar
SHOULD support arrow-key navigation between its child controls. The shipping
M1 implementation does NOT bind arrow keys inside the toolbar — focus moves
between children via Tab only. This is a known minor gap; it does not block
WCAG 2.2 SC 2.1.1 because every toolbar child is focusable via Tab.

**WCAG citations:**
- WCAG 2.2 SC 4.1.2 Name, Role, Value (`role="toolbar"` + `aria-label`).
- WCAG 2.2 SC 4.1.3 Status Messages (`aria-live="polite"` on count).

---

## 7. Loading state

The shipping implementation does **not** emit `aria-busy="true"` on the table
container during loading. This is a documented gap against DataGrid §5.

| Requirement | M1 emission | Gap |
|---|---|---|
| `aria-busy="true"` on container during loading | not emitted | **Gap** — should emit on the outer `<div>` or on `<table>`. A follow-on PR closes this. |
| Skeleton rows visually replace data | yes (6 skeleton rows) | parity |
| Header sort buttons remain focusable during loading | yes | **Deviation from DataGrid §6** — Interaction §6 documents this; the council confirms or disables. |
| Empty-state suppression while loading | yes | parity |

Without `aria-busy`, AT may announce skeleton-row content changes as if they
were real data updates. The contract names the gap so it doesn't slip.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages — programmatically
determinable busy state.

---

## 8. Empty state

When `data.length === 0 && !isLoading`, DataGrid renders a single full-width
row inside `<tbody>` containing either the `emptyState` slot or the default
`<p class="px-4 py-8 text-center text-sm text-gray-500">No results.</p>`.

| Requirement | M1 emission |
|---|---|
| Empty-state region is announced on populated → empty transition | **Not implemented** in DataGrid itself. If the host supplies a custom `emptyState` node that uses `role="status"`, it works; the default `<p>` does NOT carry a live region. |
| Empty-state visual is readable | yes (centered grey text); contrast verified ≥ 4.5:1 via the Styling token `--sf-grid-empty-message-color` on `--sf-grid-empty-bg` |
| Empty-state visible on initial render | yes |

**Gap.** Without an inherent live region on the default empty state, AT does
NOT receive an announcement when a filter eliminates all rows. The
recommended fix is wrapping the default `<p>` in `role="status"` (polite
live region) — this is non-breaking and a follow-on PR closes it.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 9. Touch targets

All interactive controls inside DataGrid MUST present a touch target of at
least 24 × 24 CSS pixels per WCAG 2.2 SC 2.5.8.

| Control | Default size at M1 |
|---|---|
| Header sort button | header cell padding `py-3 px-4` + icon `h-3 w-3` → effective target ≥ 24 × 24 ✓ |
| Per-row checkbox | native `<input type="checkbox">` with no explicit sizing; checkbox is small by default (~13 × 13). **Gap** — the spacing exception (`SC 2.5.8`) likely applies because the checkbox cell has `px-4 py-3` padding (≥ 16px horizontal + 12px vertical), but the **focusable target itself** is below 24 × 24. A follow-on PR SHOULD wrap each checkbox in a 24 × 24 click region (e.g., `<label class="inline-flex h-6 w-6 items-center justify-center">`). |
| Header tri-state checkbox | same gap as per-row checkbox |
| Bulk-actions "Clear" button | `text-sm` + no explicit padding → effective target depends on text length; likely ≥ 24 × 24 for English "Clear" but localised strings may shrink. Verify per-locale. |

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 10. Focus management

DataGrid does not explicitly manage focus across re-renders. Native focus
behaviour applies:

- **Sort activation.** The user activates a header sort button; the engine
  re-supplies sorted data; the button retains focus because the DOM node is
  not replaced. **Parity with DataGrid §9.**
- **Selection toggle.** The user toggles a row checkbox; focus stays on the
  checkbox. **Parity.**
- **Bulk-bar "Clear".** The user clicks Clear; selection resets; the toolbar
  hides; focus is lost to `<body>` (the focused button is unmounted).
  **Gap** — focus should return to a meaningful location (e.g., the table
  header, or the first row checkbox). A follow-on PR closes this.

**WCAG citation:** WCAG 2.2 SC 2.4.3 Focus Order — focus moving to `<body>` after
"Clear" is a focus management failure; the focused element is unmounted and focus
is not returned to a meaningful location. The fix is to imperatively focus a
sibling element on the Clear handler.

---

## 11. Color contrast

Per [DataGrid.Styling §4](./DataGrid.Styling.md) and the inherited
[DataGrid.Styling.md](./DataGrid.Styling.md) requirements:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Cell text on row backgrounds (default / hover / selected) | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Header text on header background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Sort indicator on header background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Cell-border colour on row background | 3:1 | WCAG 2.2 SC 1.4.11 (intentionally close to threshold for "subtle" reading) |
| Bulk-bar text on bulk-bar background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Bulk-bar bottom-border on bulk-bar background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Empty-state message colour on grid background | 4.5:1 | WCAG 2.2 SC 1.4.3 |

The default token values in `data-display.tokens.json` are pre-verified.
Provider overrides MUST re-verify.

**Color is not the only channel.** Selection state is conveyed through
checkbox state + count text live region + visual highlight. Sort state is
conveyed through `aria-sort` + indicator icon + indicator colour.

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

---

## 12. Reduced motion

The skeleton-row pulse animation (`animate-pulse`) MUST be disabled or
shortened when the user has set `prefers-reduced-motion: reduce`. Tailwind's
`motion-reduce:animate-none` variant or a global CSS rule on `@media (prefers-
reduced-motion: reduce)` is acceptable. The M1 default `animate-pulse` does
NOT honor the preference automatically — a follow-on PR SHOULD add
`motion-reduce:animate-none`.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 13. Do / Don't

### Do

- Keep `aria-sort` updates synchronous with the visual sort change so AT
  observes both at once (WCAG 2.2 SC 4.1.2).
- Provide `aria-label` on every checkbox; never rely on the column header
  alone to name the per-row checkbox.
- Use native `<button>` and `<input type="checkbox">` so platform keyboard
  activation (Space / Enter) and focus ring are inherited.
- Wrap the bulk-bar count text in `aria-live="polite"` so selection-count
  changes are announced.
- Verify every token pair in `data-display.tokens.json` meets the contrast
  rule for the surface it serves.

### Don't

- Don't omit `aria-label` from a checkbox just because the surrounding
  column has a visible header — AT doesn't always associate the two.
- Don't use `role="grid"` and the native `<table>` element together with
  conflicting role definitions; pick one pattern per release wave.
- Don't announce loading skeleton content via a live region — `aria-busy` is
  the right channel for "data fetch in progress."
- Don't let focus disappear to `<body>` after "Clear"; return focus to a
  meaningful sibling.
- Don't add `aria-selected` to rows when `selectionEnabled` is false — the
  attribute's presence implies the row is selectable.

---

## 14. Parity notes

- **Blazor (M0 DataGrid):** the framework-neutral baseline. Uses the full
  `role="grid"` pattern + two-dimensional keyboard model + per-row
  `aria-selected`.
- **React (this contract):** M1 ships with the `table` pattern + sort +
  toolbar; documents gaps against M0 honestly. The follow-on PR closes
  `aria-busy`, `aria-selected`, the empty-state live region, the bulk-bar
  focus-return, the touch-target wrapper, and `motion-reduce`.
- **Web Components (Phase M4, Lit):** TBD; the WC track adopts whichever
  baseline is canonical at M4 entry.

---

## 15. Known gaps summary (single-file index)

| # | Gap | Fix path | Tracked under |
|---|---|---|---|
| G-DGA1 | No `role="grid"` — DataGrid uses native `<table>` | **DEFERRED — council verdict G-DGA1 (2026-06-12T0052Z REVISE):** `role="grid"` migration deferred to a discrete future council-gated wave (Path B). AT-regression risk + blast radius outweigh parity benefit; APG confirms `role="grid"` is a design advantage not a conformance requirement. Six gate conditions recorded in the verdict beacon. Path A (table-pattern) is WCAG 2.2 AA conformant. **Migration prep (Conditions 1 + 6):** see `_shared/design/polish/role-grid-migration-plan.md` — test-migration plan (13 role-coupled assertions mapped across 8 render paths) + pre-flight grep baseline (Harborline library and app consumers). Conditions 2, 3, 4, 5 remain outstanding. | Semantic §3.3 + Accessibility §2 + council verdict `coordination/inbox/council-verdict-2026-06-12T0052Z-g-dga1-datagrid-role-grid.md` + migration plan `_shared/design/polish/role-grid-migration-plan.md` |
| G-DGA2 | No `aria-selected` on data rows when `selectionEnabled` | **CLOSED 2026-06-06** — `aria-selected={row.getIsSelected()}` emitted on every data `<tr>` when `selectionEnabled`; absent when not | Accessibility §4.1 |
| G-DGA3 | Per-row checkbox label uses `row.id` only | Follow-on PR adds `getRowSelectionLabel?` prop | Accessibility §4.2 |
| G-DGA4 | No `aria-busy` during `isLoading` | **CLOSED 2026-06-06** — `aria-busy="true"` on root `<div>` when `isLoading`; attribute absent otherwise | Accessibility §7 |
| G-DGA5 | Default empty-state `<p>` has no live region | **CLOSED 2026-06-06** — default `<p>` carries `role="status"`; custom `emptyState` slot is host-controlled | Accessibility §8 |
| G-DGA6 | Checkbox touch target below 24 × 24 | Follow-on PR wraps in 24 × 24 click region | Accessibility §9 |
| G-DGA7 | "Clear" button focus lost to `<body>` after activation | **CLOSED 2026-06-06** — `flushSync` + `focusTarget.focus()` returns focus to first header sort button (or table element when no sort buttons) | Accessibility §10 |
| G-DGA8 | `animate-pulse` doesn't honor `prefers-reduced-motion` | **CLOSED 2026-06-06** — `motion-reduce:animate-none` added to all skeleton bar class lists | Accessibility §12 |
| G-DGA9 | No two-dimensional keyboard navigation | **CLOSED by Path A 2026-06-12** — wave-5 keyboard matrix (Home/End/PageUp/PageDown/Ctrl+Home/Ctrl+End/Enter/Escape/ArrowKeys) implemented on the table pattern (focusable `<td tabIndex={-1}>` cells). `role="grid"` roving-tabIndex (Path B) remains deferred under G-DGA1. Implemented in PR [ref: `feat/datagrid-wave5-path-a`]. | Interaction §FS-3.5 Path A + Accessibility §FS-3.5 |
| G-DGA10 | Toolbar arrow-key navigation | Follow-on PR if toolbar grows beyond Clear + slot content | Accessibility §6 |

The contract is the source of truth for both the **shipping baseline** and the
**deferred-fix list**. Council acceptance ratifies both columns.

---

## References

- ADR 0017 §A1.3 — DataGrid contract scope (M0 framework-neutral baseline)
- [DataGrid.Accessibility.md](./DataGrid.Accessibility.md) — full grid-pattern accessibility baseline
- [DataGrid.Semantic.md](./DataGrid.Semantic.md) — prop contract
- [DataGrid.Interaction.md](./DataGrid.Interaction.md) — behavioural contract
- [DataGrid.Styling.md](./DataGrid.Styling.md) — token surface + visual states
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `grid`, `gridcell`, `columnheader`, `row`, `toolbar`, `aria-sort`, `aria-selected`, `aria-busy`, `aria-live`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages

---

## Full-surface expansion (2026-06-11 — waves 3-5, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

**Scope:** ARIA requirements for every new capability introduced in Semantic §FS-* and
Interaction §FS-*. Council question G-DGA1 (role=grid migration) is referenced throughout
but NOT decided here — both the interim `table` pattern and the target `grid` pattern are
specced side-by-side.

---

### §FS-1 Wave-3

#### §FS-1.1 Pagination pager landmarks

| Element | Role / attribute | Value | Notes |
|---|---|---|---|
| Pager root | `<nav aria-label="Pagination">` | — | Landmark identifies the pagination region to AT. |
| Page buttons | `<button type="button">` | — | Native button for keyboard activation. |
| Current page button | `aria-current="page"` | — | Identifies the active page. |
| Previous / Next | `aria-label="Previous page"` / `"Next page"` | — | Icon-only buttons must have an accessible name. |
| Disabled state | `disabled` attribute on native `<button>` | — | Native disabled suppresses from tab order and announces as "unavailable" or "dimmed". |
| Page-size selector | `<select aria-label="Rows per page">` | — | Native `<select>` is sufficient; no ARIA enhancement needed. |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 2.1.1 Keyboard.

**wave-3**

---

#### §FS-1.2 Operator-menu ARIA (filtering)

The operator-selection menu follows the WAI-ARIA menu button pattern:

| Element | Role / attribute | Notes |
|---|---|---|
| Operator trigger button | `<button type="button" aria-haspopup="menu" aria-expanded={isOpen} aria-label="Filter operator for {columnName}">` | `aria-expanded` toggles on open/close. |
| Menu container | `role="menu"` | |
| Menu item | `role="menuitemradio" aria-checked={isActive}` | Radio items because only one operator is active at a time. |
| Active operator | `aria-checked="true"` | |
| Filter value input | `aria-label="Filter value for {columnName}" aria-describedby="{operator-label-id}"` | Associates the current operator with the value input so AT announces both. |
| Validation error | `aria-describedby` pointing to the error `<span role="alert">` | Fires when an invalid filter value is committed. |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 4.1.3 Status Messages (error announcement).

**wave-3**

---

#### §FS-1.3 Selection mode ARIA

**Single mode additions:**

- Data rows in single mode are focusable (`tabIndex={0}`) and respond to Enter/Space to activate
  selection (in addition to click). `aria-selected` emits on the focused row.
- The row `role` remains `row` (table-pattern interim). When G-DGA1 adopts `role="grid"`, the
  grid composite pattern applies (see §FS-3.5 grid-role target below).

**Row-click activation (`onRowClick`):**

- When `onRowClick` is supplied, the `<tr>` elements that are clickable carry
  `style="cursor:pointer"` and `role="button"` is NOT added (they remain `role="row"` — mixing
  `role="row"` with interactive semantics is a common error; instead, the first `<td>` in each
  row receives a screen-reader-only affordance `<span class="sr-only">Activate row</span>` when
  `onRowClick` is present, visible only to AT).

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 2.1.1 Keyboard.

**wave-3**

---

#### §FS-1.4 Column reorder ARIA

| Element | Role / attribute | Notes |
|---|---|---|
| Reorderable header | `aria-grabbed` (deprecated — use drag-and-drop DnD API events) | Prefer `aria-roledescription="reorderable column"` on the `<th>` to announce the capability. |
| Drag ghost | `aria-hidden="true"` | The ghost is a visual duplicate; hide from AT. |
| Drop indicator | `aria-hidden="true"` | Visual-only affordance. |
| Live region for reorder | `<div role="status" aria-live="polite" class="sr-only">` announces `"{columnName} moved to position {n}"` after a successful drop. | Single fleet-level live region; reuse the existing `aria-live` announcement pattern. |

**Note on drag-and-drop AT support.** As of WCAG 2.2, drag-and-drop operations have a
keyboard alternative requirement (SC 2.1.1). The wave-3 spec is pointer-only. Wave-5 or a
follow-on PR must add a keyboard alternative (e.g., a "Move column" submenu in the column menu)
before this feature meets WCAG 2.2 SC 2.1.1.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard (gap noted); SC 4.1.2 Name, Role, Value.

**wave-3**

---

#### §FS-1.5 Editing mode ARIA

**Cell mode (existing + validation extension):**

- When a cell editor is open: `aria-label="Edit {columnName}"` on the `CellEditor` input.
- Validation error: the cell `<td>` emits `aria-invalid="true"` and `aria-describedby` pointing
  to the error message `<span role="alert">` rendered below the input. The alert fires on commit
  failure.
- After a successful commit: `aria-invalid` is removed; no announcement (silent success).

**Row mode additions:**

- When a row is in edit mode: `aria-label="Editing row"` on the `<tr>` element (additive; does
  not override the `row` role).
- "Save" button: `<button type="button" aria-label="Save row edits">`.
- "Cancel" button: `<button type="button" aria-label="Cancel row edits">`.
- Focus management: on entering row edit mode, focus moves to the first editable input in the
  row. On save/cancel, focus returns to the row's expand/chevron cell or, if absent, to the
  first `<td>` in the row.

**Dialog mode:**

- The dialog follows the WAI-ARIA `dialog` pattern: `role="dialog" aria-modal="true"
  aria-labelledby="{dialog-title-id}"`.
- Focus trap: focus is trapped inside the dialog while open. On close, focus returns to the "Edit"
  button that opened it.

**Batch mode bar:**

- The "Commit N changes" / "Discard" bar follows the same `role="toolbar" aria-label="Pending
  changes"` pattern as the bulk-actions bar.
- The change count is wrapped in `aria-live="polite"` so AT announces changes to the pending count.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 4.1.3 Status Messages; SC 2.1.1 Keyboard;
SC 3.3.1 Error Identification.

**wave-3**

---

#### §FS-1.6 Column menu expansion (chooser panel)

- The column-chooser flyout opened by "Columns…" follows the WAI-ARIA `dialog` pattern:
  `role="dialog" aria-label="Column visibility"`.
- Each column toggle is a native `<input type="checkbox" aria-label="{columnName}">`.
- Focus is trapped in the flyout while open; Escape closes it and returns focus to the column
  menu trigger.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

**wave-3**

---

#### §FS-1.7 Footer cells

- The `<tfoot>` element carries no additional ARIA attributes; the native element provides
  `role="rowgroup"` semantics.
- Each footer `<td>` carries `scope="col"` (or `role="columnfooter"` — this is an authoring
  practice not a formal ARIA role; use `<td>` with an `aria-label` summarising the aggregate
  if the content is not self-describing).
- Aggregate values (from §FS-2.1) in footer cells should have an accessible label:
  `aria-label="{aggregateFunction} of {columnName}: {value}"` when the cell's text content is
  a bare number.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

**wave-3**

---

### §FS-2 Wave-4

#### §FS-2.1 Group rows and aggregate ARIA

| Element | Role / attribute | Notes |
|---|---|---|
| Group `<tr>` (table-pattern interim) | `role="row"` (implicit) with `aria-expanded={isExpanded}` | `aria-expanded` on the `<tr>` announces expand/collapse state. |
| Group `<tr>` (grid-pattern target) | `role="row"` inside `role="rowgroup"` per WAI-ARIA grid spec | See G-DGA1 council gate. |
| Group chevron button | `<button type="button" aria-label="Expand group {groupKey}" aria-controls="{groupRowIds.join(' ')}">` | `aria-controls` references the ids of the child rows. Only practical for small groups (≤100 rows); for large groups use `aria-expanded` on the group row alone. |
| Aggregate cell | `aria-label="{fn} of {column}: {value}"` | Announced when AT reads the cell. |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 1.3.1 Info and Relationships.

**wave-4**

---

#### §FS-2.2 Detail rows ARIA

| Element | Role / attribute | Notes |
|---|---|---|
| Expand chevron button | `<button type="button" aria-expanded={isExpanded} aria-controls="{detailRowId}" aria-label="Expand row details">` | `aria-controls` references the `<tr id="{detailRowId}">`. |
| Detail `<tr>` | `id="{detailRowId}"` | Referenced by `aria-controls` on the chevron. |
| Detail `<td>` | `aria-label="Row details"` | Identifies the region to AT when focus enters. |
| `__expand__` column header | `<th aria-label="Row details column" scope="col">` | Column header is non-sortable; suppress sort-button semantics. |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 2.1.1 Keyboard.

**wave-4**

---

#### §FS-2.3 Row pinning ARIA

- Pinned-top rows: `aria-rowindex` values start at 1 and continue into center rows without a gap
  (the visual separation is purely styling; the logical row order includes pinned rows).
- When virtualization is active (§FS-3.4), `aria-rowindex` is required on every rendered row to
  convey the logical position within the full dataset (see §FS-3.4 below).
- No additional `role` or `aria-*` is required beyond `aria-rowindex` for pinned rows.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

**wave-4**

---

#### §FS-2.4 Stacked headers ARIA

- TanStack's header group model produces `colSpan` values on outer-group `<th>` cells
  automatically. DataGrid must pass the `colSpan` value from `header.colSpan` to the `<th>`
  element's `colSpan` attribute (this is a correctness requirement already implied by the existing
  header rendering loop — this section calls it out explicitly as a spec requirement).
- Outer group `<th>` cells that span multiple columns emit `scope="colgroup"`.
- Leaf `<th>` cells emit `scope="col"`.
- `aria-sort` emits only on leaf `<th>` cells (sortable columns only).

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

**wave-4**

---

### §FS-3 Wave-5

#### §FS-3.1 Toolbar slot ARIA

- The host-composed `toolbar` slot is wrapped in `<div role="toolbar" aria-label="Grid toolbar">`.
- Arrow-key navigation inside the toolbar: Left/Right arrow keys move focus between toolbar
  controls (WAI-ARIA toolbar composite pattern). Tab/Shift+Tab exit the toolbar.
- The toolbar does NOT use `aria-activedescendant`; it uses roving `tabIndex` (set `tabIndex=0`
  on the currently active child, `-1` on others) to comply with the toolbar pattern.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value; SC 2.1.1 Keyboard.

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

#### §FS-3.4 Virtualization ARIA

When `virtual === true`:

- Every rendered data `<tr>` emits `aria-rowindex={logicalRowIndex + 1}` (1-based per WAI-ARIA
  grid spec). The logical row index accounts for pinned rows and group rows.
- Every rendered `<td>` emits `aria-colindex={columnIndex + 1}` (1-based).
- The `<table>` (or `<div role="grid">` if G-DGA1 adopts grid pattern) emits
  `aria-rowcount={totalRowCount}` and `aria-colcount={totalColumnCount}` so AT can announce the
  full dataset dimensions even when most rows are not in the DOM.
- When `rowCount` is unknown (omitted), `aria-rowcount="-1"` (WAI-ARIA 1.2 convention for
  "unknown count").
- Skeleton rows during loading do NOT emit `aria-rowindex`; instead the container emits
  `aria-busy="true"` (already part of G-DGA4, closed in M1).

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships; WAI-ARIA 1.2 `aria-rowindex`,
`aria-colindex`, `aria-rowcount`, `aria-colcount`.

**wave-5**

---

#### §FS-3.5 Full keyboard ARIA — table-pattern interim vs. grid-pattern target

**Council item G-DGA1** decides between the two paths below. Both are specced here so the
implementation team can choose the correct path post-council without revisiting this contract.

**Path A: table-pattern interim (existing `<table>` + enhanced focus management)**

- `<td tabIndex={-1}>` on each data cell (already implemented).
- Roving `tabIndex` is NOT used for cells — all cells have `tabIndex={-1}`, focus is managed
  imperatively by the `focusedCell` state (already in the implementation).
- PageUp / PageDown / Home / End bound as described in Interaction §FS-3.5.
- `aria-sort` continues on `<th>` elements.
- No `role="grid"` or `role="gridcell"` additions — the native table elements provide row/column
  structure without the grid composite pattern overhead.

**Path B: grid-pattern target (WAI-ARIA `role="grid"`)**

- Outer `<div>` emits `role="grid"` (replaces no-role container).
- `<table>` is **removed** in favour of `<div role="grid">` with children `<div role="row">` and
  `<div role="gridcell">` (OR the `<table>` is kept and `role="grid"` is placed on the `<table>`
  directly, which is valid in ARIA 1.2 — this is the lower-risk migration path).
- Every data cell is `role="gridcell"` (or `<td role="gridcell">`).
- Roving `tabIndex` manages a single "active cell" that has `tabIndex=0`; all other cells have
  `tabIndex=-1`. Arrow keys move the active cell.
- `aria-activedescendant` on the grid container is an alternative but is less reliable across
  AT; roving `tabIndex` is preferred.
- `aria-selected` emits on focusable rows when `selectionMode !== 'none'`.
- Full two-dimensional keyboard model per WAI-ARIA 1.2 grid pattern (PageUp/Down, Home/End,
  Ctrl+Home/End as specified in Interaction §FS-3.5).

**Recommendation (non-binding, pending council):** Path A (table-interim) for wave-5 if the
council delays G-DGA1; Path B if the council adopts grid-pattern in wave-5. Path A is lower risk
and already mostly implemented; Path B is the architecturally correct destination.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard; WAI-ARIA 1.2 `grid` composite widget pattern.

**wave-5**
