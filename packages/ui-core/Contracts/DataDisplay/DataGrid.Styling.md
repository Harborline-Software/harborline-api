# DataGrid — Styling Contract

- **Component:** DataGrid
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Polish reference:** https://svar.dev/react/datagrid/ (pinned 2026-06-11, Polish-pilot — see _shared/design/polish-gate.md)
- **Polish reference (primary, KendoReact):** https://www.telerik.com/kendo-react-ui/components/grid
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DataGrid.Semantic.md) · [Interaction](./DataGrid.Interaction.md) · [Accessibility](./DataGrid.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/DataGrid.tsx`
- **Catalog row:** #35 DataGrid (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

DataGrid consumes the framework-neutral `--sf-grid-*` token surface defined in
`_shared/design/tokens/data-display.tokens.json`. This contract names the
React-track specifics: the additional surfaces DataGrid introduces beyond the
M0 baseline (the bulk-actions toolbar, the synthetic selection-checkbox column,
the skeleton-row loading pattern, and the controlled-state cell hover) and the
Tailwind class recipes that act as the M1 default styling layer.

The framework-neutral token surface is inherited; this contract MUST NOT
introduce new component-local tokens without updating
`_shared/design/tokens/data-display.tokens.json` and the parent
DataGrid.Styling contract.

---

## 2. Token surface

DataGrid consumes the full `--sf-grid-*` token family defined in
`_shared/design/tokens/data-display.tokens.json`. This contract adds **two new
sub-families** that are React-track-specific:

### 2.1 Bulk-actions toolbar

The bulk-actions toolbar is a DataGrid composition that has no analogue in the
M0 DataGrid contract. It exposes the following tokens.

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-grid-bulk-bar-bg` | Background fill of the bulk-actions toolbar | none | Distinct from `--sf-grid-row-bg-default` and the header — the toolbar reads as an action surface, typically a tinted accent (matches the selection-accent family) |
| `--sf-grid-bulk-bar-fg` | Foreground colour for the toolbar's count text and slot content | none | Pairs with `--sf-grid-bulk-bar-bg` at ≥ 4.5:1 |
| `--sf-grid-bulk-bar-border` | Bottom-border colour of the toolbar (the seam between toolbar and grid) | none | Pairs with `--sf-grid-bulk-bar-bg` at ≥ 3:1 |
| `--sf-grid-bulk-bar-clear-fg` | Foreground colour of the "Clear" link-button | default + hover | Pairs with `--sf-grid-bulk-bar-bg` at ≥ 4.5:1; hover state MAY shift hue/underline |

### 2.2 Skeleton row (loading)

DataGrid's loading affordance is a fixed-shape skeleton-row pattern (6 rows of
pulsing rectangles). It consumes the existing `--sf-grid-loading-*` tokens from
the M0 token foundation, plus one new token for the per-cell pill geometry.

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-grid-skeleton-cell-radius` | Corner radius of each per-cell skeleton pill | none | Typically resolves to `var(--sf-radius-sm)` |
| `--sf-grid-skeleton-cell-height` | Height of each per-cell skeleton pill | none | Typically `1rem` (matches default row line-height) |

The two existing tokens `--sf-grid-loading-skeleton-pulse-from` /
`--sf-grid-loading-skeleton-pulse-to` drive the pulse animation; they are NOT
re-declared here.

### 2.3 Token additions to `data-display.tokens.json`

The four `--sf-grid-bulk-bar-*` tokens and the two `--sf-grid-skeleton-cell-*`
tokens are additions to the existing `data-display.tokens.json` file. They
extend the `harborlineGrid` namespace:

```jsonc
"harborlineGrid": {
  // ... existing keys ...
  "bulkBar": {
    "_intent": "selection-active surface; tinted accent that pairs with row.bgSelected",
    "bg": "#EFF6FF",
    "fg": "#1E40AF",
    "border": "#BFDBFE",
    "clearFg": "#1D4ED8"
  },
  "skeletonCell": {
    "_intent": "per-cell pulsing pill inside skeleton rows",
    "radius": "4px",
    "height": "1rem"
  }
}
```

The default values above are derived from the shipping Tailwind classes
(`bg-blue-50`, `text-blue-800`, `border-blue-200`, `text-blue-700`,
`rounded`, `h-4`). Token-file PRs MAY ship alongside this contract or in a
follow-up; the contract is the source of truth either way.

---

## 3. Tailwind class recipes (M1 default layer)

The shipping React implementation uses Tailwind utility classes. This section
records the canonical class recipes so adapter-equivalents (CSS Modules, styled-
components, vanilla CSS) can mirror them deterministically. Each recipe maps to
the token it resolves.

| Region | Tailwind recipe (M1 default) | Token resolution |
|---|---|---|
| Root container | `flex flex-col` | layout-only — no token surface |
| Bulk-actions toolbar | `flex items-center gap-3 border-b border-blue-200 bg-blue-50 px-4 py-2 text-sm` | `--sf-grid-bulk-bar-bg` + `--sf-grid-bulk-bar-border` + `--sf-space-*` for padding |
| Toolbar count text | `font-medium text-blue-800` | `--sf-grid-bulk-bar-fg` |
| Toolbar "Clear" button | `ml-auto text-sm text-blue-700 hover:underline` | `--sf-grid-bulk-bar-clear-fg` + hover treatment |
| Table | `w-full border-collapse text-sm` | layout + typography baseline |
| Table scroll wrapper | `overflow-x-auto` | layout; no token |
| Header row | `border-b border-gray-200 bg-gray-50` | `--sf-grid-header-bg` + `--sf-grid-cell-border-color` |
| Header cell | `px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-gray-600` | `--sf-grid-header-fg` + `--sf-grid-cell-padding` |
| Header sort button | `flex items-center gap-1 hover:text-gray-900` | hover variant of `--sf-grid-header-fg` |
| Header sort icon (active) | `h-3 w-3` on `ChevronUp` / `ChevronDown` (lucide) | `--sf-grid-header-sort-indicator` (via `currentColor` from text colour) |
| Header sort icon (sortable, inactive) | `h-3 w-3 text-gray-400` on `ChevronsUpDown` | a desaturated variant of `--sf-grid-header-sort-indicator` |
| Selection checkbox column header | width pinned to `40px` via `w-10` | `--sf-grid-selection-col-width` (M1 default = `40px`; not yet a token, see §6) |
| Body row (default) | `border-b border-gray-100 hover:bg-gray-50` | `--sf-grid-row-bg-default` + `--sf-grid-row-bg-hover` + `--sf-grid-cell-border-color` |
| Body row (selected) | `bg-blue-50` (added) | `--sf-grid-row-bg-selected` |
| Body cell | `px-4 py-3 text-gray-700` | `--sf-grid-cell-padding` + global `--sf-color-text` |
| Empty-state row | `<td colSpan>` containing `<p class="px-4 py-8 text-center text-sm text-gray-500">` | `--sf-grid-empty-bg` (inherited from row default) + `--sf-grid-empty-message-color` |
| Skeleton cell wrapper | `px-4 py-3` | `--sf-grid-cell-padding` |
| Skeleton cell pill | `h-4 animate-pulse rounded bg-gray-100` | `--sf-grid-skeleton-cell-height` + `--sf-grid-skeleton-cell-radius` + `--sf-grid-loading-skeleton-pulse-from` |

### 3.1 Tailwind ↔ token mapping discipline

Per the fleet design-system convention, Tailwind classes are the M1 default
authoring layer; the token surface is the contractual layer. A consumer who
wants to re-theme DataGrid consumes the **tokens**, not the Tailwind classes.
A future provider package (FluentUI / Material / Bootstrap) MAY replace the
Tailwind recipe wholesale provided it preserves the token surface and the
visual state contract in §4.

The shipping `bg-blue-*` / `text-blue-*` palette in M1 corresponds to the
fleet's default brand-accent family. Provider themes substitute their own
accent on the same token slots.

---

## 4. Visual state inventory

DataGrid's state matrix and the token + class recipe for each state:

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | populated, no hover, no selection | body rows | `--sf-grid-row-bg-default` |
| **hover** | pointer over a body row, not loading | hovered row | `--sf-grid-row-bg-hover` via `hover:bg-gray-50` |
| **selected** | `row.getIsSelected()` is true | selected row | `--sf-grid-row-bg-selected` via `bg-blue-50` |
| **header sortable** | column `enableSorting: true`, not currently sorted | header sort button | inactive `ChevronsUpDown` at `text-gray-400` |
| **header sorted asc** | `aria-sort="ascending"` on the column | header sort button | `ChevronUp` at `currentColor` (sort indicator); the header text reads `text-gray-900` on hover |
| **header sorted desc** | `aria-sort="descending"` | header sort button | `ChevronDown` at `currentColor` |
| **focus-visible (any interactive)** | keyboard focus on a header sort button, row checkbox, or toolbar control | focused element | global `--sf-focus-ring-*` (browser default ring is the M1 baseline; provider themes override) |
| **loading** | `isLoading === true` | body | 6 skeleton rows; body is replaced wholesale; header remains in default state |
| **empty** | `data.length === 0 && !isLoading` | body | single full-width row; centred `--sf-grid-empty-message-color` text |
| **selection-active (toolbar visible)** | `selectionEnabled` and `selectedCount > 0` | toolbar above grid | `--sf-grid-bulk-bar-*` family |

**State precedence (highest first):** loading → empty → selected → hover →
default. The Interaction contract (§9 "Interaction-state precedence") owns the
behavioural ordering; this contract enforces the visual ordering.

---

## 5. Responsive behaviour

The shipping implementation uses one responsive affordance only: the table is
wrapped in `<div class="overflow-x-auto">` so narrow viewports scroll
horizontally rather than reflowing.

DataGrid does **not** at M1:

- Reflow to a card / list layout on small viewports.
- Collapse columns into a "more" menu on narrow widths.
- Switch row density (compact / default / comfortable) based on viewport.

A small-viewport reflow is a deferred enhancement; the M1 contract documents the
current state. The token surface MUST NOT add `@media`-gated tokens until that
enhancement lands.

---

## 6. Open questions

1. **Selection-column width token.** The shipping width is `40px` (`w-10`).
   Should `--sf-grid-selection-col-width` be added to the token surface, or
   does the column width stay an implementation constant? Current contract:
   implementation constant. Add a token if the design system surfaces a
   reason (e.g., a wider hit target for touch).
2. **Bulk-bar accent should it match `--sf-grid-row-bg-selected`?** The M1
   default values (`#EFF6FF` for both) coincide. The contract treats them as
   two independent tokens because a provider may want toolbar-as-call-to-action
   vs. selection-tinted-row treatments to diverge. Confirm at frontend-architect
   council.
3. **Skeleton-row count.** Fixed at 6 in M1. Should it be a `--sf-grid-skeleton-rows`
   numeric token, or driven by the host's expected page size? Current contract:
   implementation constant per DataGrid §6 "skeleton".
4. **Toolbar `aria-live` interplay with the selection-row visual.** The
   toolbar's count text is `aria-live="polite"` (per Accessibility); the
   selection-row background flip is a non-announced visual. The combination
   meets WCAG, but design may want the row treatment to be more emphatic
   (e.g., a left-accent stripe rather than full-row tint). Out of scope for
   M1 token surface; revisit if usability surfaces a gap.

---

## 7. Do / Don't

### Do

- Consume `--sf-grid-*` via the recipes in §3; treat the Tailwind classes as
  the M1 default authoring layer.
- Pair `--sf-grid-row-bg-default` / `-hover` / `-selected` so the progression
  is perceivable at every provider — verify in `data-display.tokens.json`.
- Pair `--sf-grid-bulk-bar-bg` and `--sf-grid-bulk-bar-fg` such that count
  text + slot content meet WCAG 2.2 SC 1.4.3 (≥ 4.5:1).
- Mark the header sort icon's colour from `currentColor` so a single hover /
  focus change re-themes both text and indicator.
- Honor `prefers-reduced-motion: reduce` on the skeleton-pulse animation —
  disable `animate-pulse` or shorten to ≤ 100 ms cross-fade.

### Don't

- Don't put concrete hex values, RGB tuples, or other colour primitives in
  this contract or in `DataGrid.tsx`. Hex values belong only in
  `data-display.tokens.json`.
- Don't add a hover state to the bulk-actions toolbar's count-text region —
  the count is not interactive. Only the "Clear" button has a hover.
- Don't introduce a new component-local token without adding it to §2 and to
  `data-display.tokens.json`.
- Don't bake the M1 brand-blue (`bg-blue-50`, `text-blue-700`) directly into
  consumer code outside DataGrid. Consumers theme via the token surface.
- Don't drive the row-selected visual from a CSS class (`.is-selected`)
  alone — keep parity with `[aria-selected="true"]` per the accessibility
  contract (see Accessibility §3).

---

## 8. Parity notes

- **Blazor (M0 DataGrid):** consumes the same `--sf-grid-*` family via
  scoped `.razor.css`. No bulk-actions toolbar in the M0 spec; if the Blazor
  track adopts the React selection-bar pattern, the `--sf-grid-bulk-bar-*`
  tokens added here are the shared surface.
- **React (this contract):** consumes via Tailwind utility classes as the M1
  default. A Phase M2 CSS-Modules layer MAY supersede the inline Tailwind
  recipe; the token surface is unchanged.
- **Web Components (Phase M4, Lit):** consumes inside Shadow DOM; the
  `::part(bulk-bar)`, `::part(row)`, `::part(cell)` exposure map (TBD) lets
  consumers override the toolbar and inner regions without violating
  encapsulation.

---

## References

- ADR 0017 §A1.3 — DataGrid contract scope (M0 token-surface foundation)
- [DataGrid.Semantic.md](./DataGrid.Semantic.md) — prop contract
- [DataGrid.Interaction.md](./DataGrid.Interaction.md) — state-precedence rules
- `_shared/design/tokens/data-display.tokens.json` — concrete default values
- `_shared/design/tokens-guidelines.md` — `--sf-*` token naming conventions
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.3.3 Animation from Interactions

---

## Full-surface expansion (2026-06-11 — waves 3-5, spec-first)

**Status:** Draft (spec-first; promote on wave acceptance)

**Reference:** KendoReact Grid (https://www.telerik.com/kendo-react-ui/components/grid) — primary
visual target for token values. Kendo uses its own design tokens; the mapping below maps Kendo's
visual intent to our `--sf-grid-*` namespace and Tailwind class recipes.

**Convention:** All new tokens MUST be added to `_shared/design/tokens/data-display.tokens.json`
under the `harborlineGrid` namespace before the implementing PR merges. The canonical default values
listed below are derived from Kendo's visual treatment and the fleet's existing blue-accent palette.

---

### §FS-1 Wave-3 token additions

#### §FS-1.1 Pager band

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-pager-bg` | Background of the built-in pager footer band | `#F9FAFB` (`gray-50`) | Matches header background for visual bookending. |
| `--sf-grid-pager-fg` | Foreground (text) colour of pager controls | `#374151` (`gray-700`) | |
| `--sf-grid-pager-border` | Top-border of the pager band (seam with table body) | `#E5E7EB` (`gray-200`) | Matches `--sf-grid-cell-border-color`. |
| `--sf-grid-pager-btn-bg-active` | Background of the current-page button | `#EFF6FF` (`blue-50`) | Matches selection accent. |
| `--sf-grid-pager-btn-fg-active` | Foreground of the current-page button | `#1D4ED8` (`blue-700`) | |
| `--sf-grid-pager-btn-bg-hover` | Background of a non-active page button on hover | `#F3F4F6` (`gray-100`) | |

**Tailwind recipe:**

| Region | Recipe |
|---|---|
| Pager root | `flex items-center justify-between border-t border-gray-200 bg-gray-50 px-4 py-2 text-sm text-gray-700` |
| Page button (default) | `rounded px-2 py-1 hover:bg-gray-100` |
| Page button (active) | `rounded bg-blue-50 px-2 py-1 font-medium text-blue-700` |
| Page-size selector | `rounded border border-gray-200 bg-white px-1 py-0.5 text-xs outline-none focus:ring-1 focus:ring-blue-400` |

**wave-3**

---

#### §FS-1.2 Operator-menu filter input

The operator trigger button and operator menu reuse the existing `ColumnMenu` visual treatment
(same `min-w-[160px] rounded-md border border-gray-200 bg-white shadow-lg` recipe). No new
tokens required beyond:

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-filter-operator-btn-fg` | Foreground colour of the funnel icon button | `#9CA3AF` (`gray-400`) | Same as inactive sort indicator to match visual weight. |
| `--sf-grid-filter-operator-btn-fg-active` | Foreground when the column has an active filter | `#2563EB` (`blue-600`) | Communicates "this column is filtered" without relying on colour alone (icon changes to filled funnel). |
| `--sf-grid-filter-value-border-focus` | Border colour of filter value input on focus | `#60A5FA` (`blue-400`) | Matches the existing cell-editor focus ring. |

**wave-3**

---

#### §FS-1.4 Drag-reorder drop indicator

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-reorder-indicator-color` | Colour of the vertical drop-indicator line between columns | `#2563EB` (`blue-600`) | High-contrast vs. both `gray-50` header and white body. |
| `--sf-grid-reorder-indicator-width` | Width of the indicator line | `2px` | |
| `--sf-grid-drag-ghost-opacity` | Opacity of the drag ghost | `0.7` | Semi-transparent to allow the drop target to show through. |

**Tailwind recipe:**

| Region | Recipe |
|---|---|
| Drag ghost | `opacity-70 shadow-md ring-1 ring-blue-400` applied to the ghost `<div>` |
| Drop indicator | `absolute inset-y-0 w-0.5 bg-blue-600 z-30` positioned at the drop seam |

**wave-3**

---

#### §FS-1.5 Editing visual states

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-edit-cell-border` | Border colour of an active cell editor | `#60A5FA` (`blue-400`) | Matches existing `CellEditor` implementation. |
| `--sf-grid-edit-cell-error-border` | Border colour of a cell with a validation error | `#F87171` (`red-400`) | |
| `--sf-grid-edit-cell-error-fg` | Colour of the inline error message text | `#DC2626` (`red-600`) | Pairs with white background at ≥ 4.5:1. |
| `--sf-grid-batch-bar-bg` | Background of the batch-pending commit/discard bar | `#FFFBEB` (`amber-50`) | Warm tint distinguishes it from the blue bulk-actions bar. |
| `--sf-grid-batch-bar-fg` | Foreground of batch bar text | `#92400E` (`amber-800`) | Pairs with `amber-50` at ≥ 4.5:1. |
| `--sf-grid-batch-bar-border` | Bottom-border of batch bar | `#FCD34D` (`amber-300`) | |
| `--sf-grid-pending-cell-indicator` | Left-accent stripe colour on dirty (pending) cells | `#F59E0B` (`amber-400`) | 3px left border on the `<td>`. |
| `--sf-grid-row-edit-bg` | Background of a row in row-edit mode | `#EFF6FF` (`blue-50`) | Matches `--sf-grid-row-bg-selected`; edit mode is visually consistent with selection. |

**Kendo reference:** Kendo uses an amber/yellow dirty indicator on batch-edited cells
(see Kendo Grid Batch Editing demo) and a blue row-level highlight for in-row editing.

**Tailwind recipes:**

| Region | Recipe |
|---|---|
| Batch bar | `flex items-center gap-3 border-b border-amber-300 bg-amber-50 px-4 py-2 text-sm text-amber-800` |
| Pending cell left-accent | `border-l-2 border-l-amber-400` added to `<td>` classNames |
| Row in edit mode | `bg-blue-50` on the `<tr>` |
| Validation error span | `block mt-0.5 text-xs text-red-600` |

**wave-3**

---

#### §FS-1.7 Footer band

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-footer-bg` | Background of the `<tfoot>` row | `#F9FAFB` (`gray-50`) | Matches header background. |
| `--sf-grid-footer-fg` | Foreground text colour | `#374151` (`gray-700`) | Slightly heavier than body cells to indicate summary. |
| `--sf-grid-footer-border` | Top-border of the footer band | `#D1D5DB` (`gray-300`) | Slightly darker than row borders to visually anchor the footer. |
| `--sf-grid-footer-font-weight` | Font weight of footer cell text | `600` (semibold) | Kendo renders aggregate values in semibold. |

**Tailwind recipe:**

| Region | Recipe |
|---|---|
| `<tfoot>` row | `border-t border-gray-300 bg-gray-50` |
| Footer `<td>` | `px-4 py-2 text-sm font-semibold text-gray-700` |

**wave-3**

---

### §FS-2 Wave-4 token additions

#### §FS-2.1 Group row background + indent

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-group-row-bg` | Background of group summary rows | `#F3F4F6` (`gray-100`) | Visually distinct from data rows (`gray-50` hover). |
| `--sf-grid-group-row-fg` | Foreground of group label and aggregate text | `#111827` (`gray-900`) | Stronger weight than data rows. |
| `--sf-grid-group-row-border` | Bottom-border of group rows | `#D1D5DB` (`gray-300`) | Heavier than standard cell border. |
| `--sf-grid-group-indent-size` | Left indent per nesting level | `1.5rem` | Applied via `paddingLeft: depth * 1.5rem` on the group-label cell. |
| `--sf-grid-group-chevron-fg` | Colour of the expand/collapse chevron icon | `#6B7280` (`gray-500`) | |
| `--sf-grid-group-aggregate-fg` | Colour of aggregate values in group rows | `#374151` (`gray-700`) | Slightly lighter than the group label; italics are optional. |

**Kendo reference:** Kendo renders group rows in a distinctly tinted band with the group key
bold-left and aggregate values right-aligned in each column cell.

**Tailwind recipe:**

| Region | Recipe |
|---|---|
| Group `<tr>` | `border-b border-gray-300 bg-gray-100` |
| Group label cell | `px-4 py-2 text-sm font-semibold text-gray-900` + `paddingLeft: {depth * 1.5}rem` inline |
| Group chevron | `h-4 w-4 text-gray-500 transition-transform` + `rotate-90` when expanded |
| Aggregate cell | `px-4 py-2 text-sm text-gray-700` |

**wave-4**

---

#### §FS-2.2 Detail-row region

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-detail-row-bg` | Background of the detail row `<tr>` | `#FAFAFA` (near-white) | Slightly off-white to read as a subordinate region. |
| `--sf-grid-detail-row-border` | Left-accent stripe on the detail row | `#93C5FD` (`blue-300`) | 3px left border marks the parent-child relationship. |
| `--sf-grid-detail-row-padding` | Padding inside the detail cell | `1rem 1.5rem` | |
| `--sf-grid-expand-chevron-size` | Width of the `__expand__` column | `36px` | Narrower than the select column (`48px`). |

**Tailwind recipe:**

| Region | Recipe |
|---|---|
| Detail `<tr>` | `border-b border-gray-100 bg-[#FAFAFA]` |
| Detail `<td>` | `border-l-2 border-l-blue-300 px-6 py-4` |
| Expand chevron button | `flex h-6 w-6 items-center justify-center rounded hover:bg-gray-100` |

**wave-4**

---

#### §FS-2.3 Pinned-row elevation

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-pinned-row-bg` | Background of pinned rows (top and bottom) | `#FEFCE8` (`yellow-50`) | Subtle warm tint differentiates from center rows. Kendo uses a light blue; fleet uses warm-tint to not conflict with the blue selection/bulk-bar palette. |
| `--sf-grid-pinned-row-border` | Bottom-border of pinned-top / top-border of pinned-bottom rows | `#FDE68A` (`yellow-200`) | Heavier than standard row border. |
| `--sf-grid-pinned-row-shadow` | Box-shadow on pinned rows | `0 2px 4px rgba(0,0,0,0.06)` | Subtle elevation reading above center rows. |

**Tailwind recipe:**

| Region | Recipe |
|---|---|
| Pinned-top `<tr>` | `bg-yellow-50 shadow-sm` + `border-b-2 border-b-yellow-200` |
| Pinned-bottom `<tr>` | `bg-yellow-50 shadow-sm` + `border-t-2 border-t-yellow-200` |

**wave-4**

---

#### §FS-2.4 Stacked header bands

No new tokens required — stacked headers reuse existing header tokens:

- `--sf-grid-header-bg` fills every `<th>` in every header group row.
- Column-group `<th>` cells that span multiple columns centre their label text:
  `text-center` added to the class recipe for non-leaf `<th>` cells.
- A subtle bottom-border separates outer group rows from leaf header rows:
  `border-b border-gray-300` on non-last header group rows.

**wave-4**

---

### §FS-3 Wave-5 token additions

#### §FS-3.1 Toolbar band

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-toolbar-bg` | Background of the host-composed toolbar band | `#FFFFFF` (white) | Neutral so host content stands out. |
| `--sf-grid-toolbar-border` | Bottom-border of the toolbar band | `#E5E7EB` (`gray-200`) | Same as column separator weight. |
| `--sf-grid-toolbar-padding` | Padding inside the toolbar band | `0.5rem 1rem` | |

**Tailwind recipe:**

| Region | Recipe |
|---|---|
| Toolbar `<div>` | `flex items-center gap-3 border-b border-gray-200 bg-white px-4 py-2` |

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

#### §FS-3.2 Virtualized body

No new tokens for the virtualized body — the standard `--sf-grid-row-*` tokens apply to
virtualised rows identically. One new constant:

| Token | Semantic role | Default value | Notes |
|---|---|---|---|
| `--sf-grid-virtual-overscan` | Number of overscan rows above and below the visible window | `5` | Implementation constant; not a visual token but documented here for tuning guidance. |

The `overflow-auto` wrapper replaces `overflow-x-auto` when `virtual === true`. The scroll
container must have an explicit `height` set by the host (or `max-height`); DataGrid adds
`height: 100%` to the inner virtual body region.

**wave-3** (promoted from wave-5 per CIC 2026-06-11 — toolbar visibility/effort)

---

### §FS state-precedence update

Extends §4 "Visual state inventory" with the new states:

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **group-row** | row is a group row (from §FS-2.1) | group `<tr>` | `--sf-grid-group-row-bg` |
| **detail-row** | row is a detail row (from §FS-2.2) | detail `<tr>` | `--sf-grid-detail-row-bg` |
| **pinned-top** | row in `rowPinning.top[]` (from §FS-2.3) | pinned `<tr>` | `--sf-grid-pinned-row-bg` |
| **pinned-bottom** | row in `rowPinning.bottom[]` | pinned `<tr>` | `--sf-grid-pinned-row-bg` |
| **row-edit** | row is in row-edit mode (from §FS-1.5) | edited `<tr>` | `--sf-grid-row-edit-bg` |
| **pending-cell** | cell has a pending batch change (from §FS-1.5) | edited `<td>` | `--sf-grid-pending-cell-indicator` left stripe |
| **cell-error** | validation failed on cell commit | error `<td>` | `--sf-grid-edit-cell-error-border` |
| **toolbar-visible** | `toolbar` prop supplied | toolbar band above grid | `--sf-grid-toolbar-bg` |
| **batch-bar-visible** | `pendingChanges` is non-empty | batch bar above grid | `--sf-grid-batch-bar-bg` |

**Full state precedence (highest first):**
loading → batch-bar-visible → toolbar-visible → empty → cell-error → row-edit → pending-cell
→ selected → pinned-top / pinned-bottom → group-row → detail-row → hover → default.

The token surface and Interaction §9 "Interaction-state precedence" align on this ordering.
