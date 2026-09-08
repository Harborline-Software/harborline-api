# Pager — Semantic Contract

- **Component:** Pager
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Pager.Interaction.md) · [Styling](./Pager.Styling.md) · [Accessibility](./Pager.Accessibility.md)
- **Related contract:** [DataGrid.Semantic.md](./DataGrid.Semantic.md) — Pager is typically composed below DataGrid.
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Pager.tsx`
- **Catalog row:** #93 Pager (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled button-based navigator
- **Companion in framework-neutral catalog:** see also the framework-neutral `Pager` (Navigation family in `M0-backlog.md` row 6); the React-track realisation lives in the DataDisplay family because it physically composes with DataGrid.

---

## 1. Purpose

Pager is the canonical pagination control of `@harborline-software/ui-react`. It is a
**stateless display + emit** component: the host owns the `page`, `pageSize`, and
`total` state; Pager renders the affordances (prev / next, page-size selector,
"Showing X–Y of Z" summary) and emits change callbacks back to the host.

Pager is intentionally decoupled from DataGrid so the same component can drive
pagination for any paged surface (cards, list views, search results) — not only
tabular data.

---

## 2. Data model

Pager has no internal data model. It computes derived display values from the
host-supplied props:

```typescript
// Derived values computed inside Pager (not exposed as props):
//   pageCount = max(1, ceil(total / pageSize))
//   from = total === 0 ? 0 : (page - 1) * pageSize + 1
//   to   = min(page * pageSize, total)
//   canPrev = page > 1
//   canNext = page < pageCount
```

```typescript
interface PagerProps {
  page: number                                    // 1-based current page
  pageSize: number                                // rows per page
  total: number                                   // total record count across all pages
  onPageChange: (page: number) => void            // fires when the user navigates
  onPageSizeChange?: (pageSize: number) => void   // optional; renders the page-size selector when supplied
  pageSizeOptions?: number[]                      // default [10, 25, 50, 100]
  mode?: 'prev-next' | 'numbered'                 // default 'numbered'
  buttonCount?: number                            // numbered mode: max page buttons in window; default 7 (must be odd)
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `page` | `number` | _required_ | **1-based** current page index. Host controls; Pager displays. Values outside `[1, pageCount]` are not corrected — the host is responsible for clamping. |
| `pageSize` | `number` | _required_ | Rows per page. Used to compute `pageCount`, `from`, `to`. |
| `total` | `number` | _required_ | Total record count across all pages. `total === 0` is a valid empty state; Pager renders "No results" in place of "Showing X–Y of Z". |
| `onPageChange` | `(page: number) => void` | _required_ | Fired when the user clicks a page button, Previous, or Next. Payload is the new **1-based** page index. |
| `onPageSizeChange` | `(pageSize: number) => void` | — | Optional. When supplied, Pager renders a "Rows per page" selector. When omitted, the selector is hidden. |
| `pageSizeOptions` | `number[]` | `[10, 25, 50, 100]` | Page-size options the selector offers. Ignored when `onPageSizeChange` is omitted. |
| `mode` | `'prev-next' \| 'numbered'` | `'numbered'` | Navigation affordance style. `'numbered'` (default) renders a sliding window of numbered page buttons with ellipsis for collapsed ranges. `'prev-next'` renders the legacy "Page X of Y" text between Prev / Next buttons. |
| `buttonCount` | `number` | `7` | Numbered mode only. Controls the maximum number of page buttons shown in the sliding window. Should be odd for visual symmetry. The first and last pages are always shown outside the window count. |

### 3.1 1-based page indexing

Pager uses **1-based** page indexing (page 1, 2, 3, …) matching DataGrid §4
and user-facing display conventions ("Page 1 of 5"). Hosts integrating with
0-based server APIs must translate at the boundary.

### 3.2 No internal clamping

If the host supplies `page > pageCount` (e.g. the data set shrank), Pager does
**not** auto-correct — it will render "Page N of M" with `N > M`. The host is
expected to clamp on data-change. This is a known sharp edge and may be
revisited (open question §8).

### 3.3 HTML attribute passthrough

The current implementation does **not** spread arbitrary HTML attributes on the
root element. Like DataGrid, this is a known gap against the DataGrid §3.1
"stable anchor + host-attribute splat" rule. A future contract amendment will
close it.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onPageChange` | `(page: number)` | The user activates Previous (`page - 1`) or Next (`page + 1`). 1-based. Pager does not emit when the navigation would step out of `[1, pageCount]` — the buttons are disabled. |
| `onPageSizeChange` | `(pageSize: number)` | The user selects a different page-size from the selector. Pager does **not** simultaneously emit `onPageChange` to reset `page` to 1 — the host owns that policy. |

**Host responsibility on `onPageSizeChange`:** changing page-size may invalidate
the current `page` (e.g. you were on page 5 of 10 at size 10, you switch to
size 50 — page 5 no longer exists). The host typically resets `page` to 1 on
this callback. Pager does not enforce this.

---

## 5. Slots

Pager has **no slot extensibility** in M1. The internal regions (page-size
selector, range summary, navigation buttons) are fixed in placement and content.

Slotting for custom content (e.g. a "jump to page" input, a custom page-number
list) is a deferred feature (§7).

---

## 6. Component composition

- **DataGrid pairing.** Pager is typically rendered immediately below
  DataGrid, with the host computing `page` / `total` / `pageSize` state and
  feeding paginated `data` to DataGrid. The two components do not communicate
  directly — the host is the integration point.
- **Generic surface.** Pager works equally well below any paged surface (list
  view, search results, card grid). It does not assume tabular data.
- **Navigation modes.** Default `mode='numbered'` renders a sliding window of
  numbered page buttons (first + last page always visible; ellipsis for
  collapsed ranges). `mode='prev-next'` is the legacy minimal style. First /
  Last jump buttons remain deferred (§7).

---

## 7. Deferred features

Explicitly **out of scope** for the M1 baseline:

- **First / Last navigation buttons** beyond Prev / Next.
- ~~**Numbered page-link list** (1 / 2 / 3 / … / N).~~ **CLOSED — G-PG1.**
  Implemented as `mode='numbered'` (now the default). See §3 `mode` +
  `buttonCount` props.
- **"Jump to page" direct input.** **(G-PG2 — deferred)** Telerik Pager
  has `InputType=PagerInputType.Input` for direct page entry. Our Pager
  does not have a built-in text input. Hosts who need this today can render
  a native `<input type="number">` alongside the Pager and call
  `onPageChange(Number(e.target.value))` on blur. Planned as a `mode='input'`
  variant in M1.1.**
- **Internal `page` clamping** when `page > pageCount`.
- **HTML attribute passthrough on root.**
- **Custom slot content** for "Rows per page" label or range summary.
- **Locale-aware number formatting** in the "Showing X–Y of Z" range summary
  (currently uses default JS number rendering).
- **Responsive / narrow-viewport behavior (closes G-PG3)** — on narrow
  viewports the numbered-mode page-button window may overflow its container
  and cause horizontal scroll. There is no automatic collapse to
  `mode='prev-next'` or a smaller `buttonCount` at breakpoints in M1.
  **Workaround:** drive `buttonCount` from a breakpoint observer
  (`buttonCount={isMobile ? 3 : 7}`) or conditionally swap `mode` via a
  media-query hook. A built-in responsive variant is deferred.
- **"Show all" page-size option (closes G-PG4)** — passing `0` or a
  `null`/`'all'` sentinel in `pageSizeOptions` to represent "show all
  records on one page" is **not supported** in M1. Every entry in
  `pageSizeOptions` must be a positive integer. **Workaround:** include
  a sufficiently large count (e.g. `total` or a business-rule max like
  `10000`) as the last option, or render a native `<select>` outside
  Pager with the "All" entry that sets `pageSize` to `total` in host
  state. A built-in `null` / `'all'` sentinel entry is deferred.

---

## 8. Open questions (for council)

1. **Page clamping.** Should Pager auto-clamp `page` into `[1, pageCount]` and
   emit a corrective `onPageChange(clamped)` when out of range? Or is the
   current "host owns clamping" stance correct? (Leaning: keep host-owned for
   M1; revisit if real hosts hit the sharp edge.)
2. **Page-size change → page reset.** Should Pager auto-emit
   `onPageChange(1)` whenever `onPageSizeChange` fires? Or remain host-owned?
   (Leaning: keep host-owned; the policy varies — some hosts want to preserve
   the visible record across page-size changes.)
3. **Range-summary live region.** The "Showing X–Y of Z" span has
   `aria-live="polite"` and the "Page X of Y" span has `aria-live="polite"` —
   announcing both on every page change is verbose. Should one of them drop
   the live attribute? (Leaning: keep both for now; PAO Accessibility
   contract owns the final call.)

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/datagrid/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PG1 | Critical | Numbered page-link buttons deferred — dominant pattern | [RESOLVED 2026-06-05] §3 `mode` prop added; §7 numbered mode is M1.1 roadmap |
| G-PG2 | Critical | InputType (text-input page-jumper) absent | [RESOLVED 2026-06-05] §7 + §3 `mode` prop: jumper deferred; same M1.1 roadmap |
| G-PG3 | High | Responsive/AdaptiveMode absent | [RESOLVED 2026-06-05] §7: overflow on mobile noted; horizontal scroll fallback in M1 |
| G-PG4 | High | pageSizeOptions null/"All" entry absent | [RESOLVED 2026-06-05] §3: number[] only; hosts drive pageSize=totalCount for "All" |
| G-PG5 | Medium | aria-live dual-region OQ unresolved | [ACCEPTED-RISK 2026-06-05] §8.3: single live region recommended; resolved |
| G-PG6 | Medium | Locale number formatting | [ACCEPTED-RISK 2026-06-05] §7 note: use toLocaleString() for Showing X-Y of Z text |
| G-PG7 | Medium | Refresh button (Telerik) | [ACCEPTED-RISK 2026-06-05] N/A — no Harborline equivalent planned |
