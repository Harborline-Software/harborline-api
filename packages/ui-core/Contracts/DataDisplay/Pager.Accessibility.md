# Pager — Accessibility Contract

- **Component:** Pager
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Pager.Semantic.md) · [Interaction](./Pager.Interaction.md) · [Styling](./Pager.Styling.md)
- **Related contract:** [DataGrid.Accessibility.md](./DataGrid.Accessibility.md) — Pager composes below DataGrid; this contract names the focus-coordination expectations between them.
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Pager.tsx`
- **Catalog row:** #93 Pager (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

Pager is a small, native-control-heavy component. Most of its accessibility
surface comes "for free" from the underlying `<button>` and `<select>`
elements. This contract:

1. Documents the live-region pattern that announces page-position and
   summary changes.
2. Names the focus-management expectation across page-change re-renders
   (which is where the host-controlled state model intersects accessibility).
3. Records the touch-target geometry of the chevron buttons.
4. Documents two minor gaps against the WCAG 2.2 AA baseline that follow-on
   PRs should close.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. ARIA structural roles

Pager uses native HTML control elements; ARIA roles are implicit:

| Element | Implicit role | Explicit role override |
|---|---|---|
| Root container | `<div>` (no role) | **None.** The container is a layout grouping, not a navigation landmark. See §11. |
| Page-size selector | `<select>` — `combobox` (HTML semantics) | none |
| Page-size selector options | `<option>` — `option` | none |
| Summary text | `<span aria-live="polite">` | `status` (implicit via `aria-live`) |
| Previous / Next buttons | `<button type="button">` — `button` | none |
| Page-position text | `<span aria-live="polite">` | `status` (implicit via `aria-live`) |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value — native HTML
elements provide implicit role and name (where supplied).

---

## 3. Accessible names

| Control | Accessible name source | M1 baseline |
|---|---|---|
| Page-size selector | implicit from the `"Rows per page"` text label adjacent to it | **Gap** — the label is a sibling `<span>`, not an associated `<label>`. AT does NOT reliably associate them. The select element has no explicit `aria-label`. See §10 (gap G1). |
| Previous button | `aria-label="Previous page"` | parity |
| Next button | `aria-label="Next page"` | parity |
| Summary text | content itself ("Showing X–Y of Z" or "No results") | parity |
| Page-position text | content itself ("Page N of M") | parity |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Live regions

Pager announces two stateful changes via polite live regions:

| Live region | Element | Politeness | Announces |
|---|---|---|---|
| Summary | `<span aria-live="polite">` left cluster | polite | "Showing X–Y of Z" content changes on page or page-size change; "No results" on `total === 0` |
| Page-position | `<span aria-live="polite">` right cluster | polite | "Page N of M" content changes on page change |

Both regions are present on every render (they're not conditionally mounted),
so the live-region "first announcement on insertion" surprise does not apply
— AT observes the existing region and announces the textContent diff on each
change.

**Politeness rationale.** Pagination changes are user-initiated; "polite"
(queued until idle) is the correct cadence. `"assertive"` would interrupt the
user's reading flow on every page click and is wrong here.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

### 4.1 Double-announcement consideration

Both the summary and page-position live regions update on every page change.
A user with a screen reader hears, roughly:

> "Showing 11 to 20 of 100" … "Page 2 of 10"

That is verbose but acceptable — the two pieces of information are
complementary, not redundant (one is record range, the other is page index).
The contract does NOT recommend merging them.

---

## 5. Keyboard navigation

Pager uses native focusable controls; keyboard navigation follows the
platform tab order:

| Key | Behaviour |
|---|---|
| Tab | Move forward through Pager's focusable controls: page-size `<select>` → Previous button → Next button → next focusable element after Pager. |
| Shift+Tab | Reverse order. |
| Enter (on a button) | Activate Previous or Next. |
| Space (on a button) | Activate Previous or Next (native button activation). |
| ArrowUp / ArrowDown (on the `<select>`) | Cycle through page-size options (native `<select>` keyboard handling). |
| Enter (on a `<select>` option) | Commit the selected page size (native). |
| Escape (on a `<select>`) | Cancel the dropdown (native). |

There is no Pager-specific keyboard handler. Disabled chevron buttons are
skipped in the tab order (per native `<button disabled>` semantics).

**WCAG citations:** WCAG 2.2 SC 2.1.1 Keyboard; WCAG 2.2 SC 2.1.2 No
Keyboard Trap.

---

## 6. Focus management

Page-change events fire `onPageChange(page)`; the host re-supplies `page`;
Pager re-renders. The focus-management expectation is:

- **Previous click.** The Previous button retains focus after re-render, UNLESS
  the user has clicked to page 1 (Previous becomes disabled). When that
  transition happens, **focus is lost** because a disabled `<button>` is not
  focusable. **Gap G2** (§10) — focus should move to a sibling element
  (e.g., the Next button if it's enabled, or the page-position text via
  `tabIndex={-1}` + programmatic focus).
- **Next click.** Symmetric: focus is lost when the user pages to the last
  page and Next becomes disabled. **Same gap G2.**
- **Page-size change.** The `<select>` retains focus after re-render
  (host-controlled state model; the `<select>` DOM node is not replaced).
  **Parity** with WCAG 2.2 SC 3.2.2.
- **Composing-DataGrid focus.** When Pager fires `onPageChange` and the host
  refetches data + the DataGrid re-renders with a new row set, focus
  remains on the activated control (Previous / Next / `<select>`). The
  DataGrid does NOT steal focus.

**WCAG citation:** WCAG 2.2 SC 3.2.2 On Input — page-change activations
MUST NOT move focus to an unexpected location. The disabled-chevron gap
(G2) is a documented violation; the contract names the fix.

---

## 7. Touch targets

| Control | Default size at M1 |
|---|---|
| Previous / Next chevron button | `p-1` padding (4px) + `ChevronLeft` / `ChevronRight` `h-4 w-4` (16 × 16) → effective target 24 × 24 ✓ |
| Page-size `<select>` | `px-1.5 py-0.5` padding (6px horizontal, 2px vertical) + native text "10" / "25" / ... → effective target depends on `text-sm` line height. For typical English numerals, ≥ 24 × 24. **Gap G3** (§10) — verify per-locale; consider widening padding. |

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 8. Color contrast

Per [Pager.Styling §"Color contrast"](./Pager.Styling.md):

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Summary text / page-position text on Pager background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Top-edge divider on Pager background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Chevron icon on Pager background (enabled state) | 3:1 | WCAG 2.2 SC 1.4.11 |
| `<select>` border on `<select>` background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Disabled chevron icon | exempt from contrast minimums per WCAG 2.2 SC 1.4.3 Notes |

**Color is not the only channel.** The disabled state of a chevron is
conveyed through three channels: (1) lowered opacity via
`disabled:opacity-40`, (2) `cursor: not-allowed`, (3) the `disabled` HTML
attribute which AT announces. **WCAG 2.2 SC 1.4.1 satisfied.**

---

## 9. Reduced motion

Pager has no animations in M1. The contract notes this for forward-compat:
any future hover/focus/page-change transition MUST honor
`prefers-reduced-motion: reduce`.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 10. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | Page-size `<select>` has no explicit `aria-label`; sibling `<span>` "Rows per page" is not associated via `<label>` | Follow-on PR adds `aria-label="Rows per page"` to the `<select>` (or wraps it in `<label>`) |
| G2 | Focus is lost when Previous / Next becomes disabled (user pages to first/last) | Follow-on PR redirects focus to a sibling (the still-enabled chevron, or the page-position text via `tabIndex={-1}`) |
| G3 | Page-size `<select>` touch target may be below 24 × 24 for narrow locales | Follow-on PR widens `<select>` padding or sets `min-width` |
| G4 | No Pager-level `aria-label` on the root `<div>` for navigation-landmark consumers | Defer — Pager is not a landmark; if hosts want it landmark-tagged, they wrap externally |

---

## 11. Landmark consideration

Pager is intentionally NOT a `<nav>` landmark in M1. Pagination is a
secondary navigation surface, and elevating every paged grid to a navigation
landmark would pollute the document's landmark map for AT users (a typical
ERP page has 4–8 paged grids; that becomes 4–8 navigation landmarks).

Hosts that want landmark exposure can wrap Pager:

```tsx
<nav aria-label="Tenants pagination">
  <Pager {...} />
</nav>
```

The contract treats this as host-side opt-in, not Pager's default.

---

## 12. Do / Don't

### Do

- Use native `<button>` and `<select>` so platform keyboard activation and
  AT exposure are inherited.
- Provide `aria-label` on every chevron button. The lucide icon is
  `aria-hidden="true"` already.
- Use `aria-live="polite"` on both summary and page-position text regions.
- Disable Previous / Next via the `disabled` HTML attribute, not by
  attaching an `onClick` no-op — AT and keyboard semantics depend on the
  native disabled state.
- Verify token contrast pairs in `pagination.tokens.json` before merging
  provider overrides.

### Don't

- Don't trap focus inside Pager — Tab MUST exit normally to the next
  focusable element after the Next button.
- Don't announce page-change via `aria-live="assertive"` — pagination is
  user-initiated and polite is the right cadence.
- Don't bind keyboard handlers to the root container — every control is
  natively focusable and natively keyboard-activatable; an extra layer
  introduces bug surface.
- Don't render the page-size selector when `onPageSizeChange` is omitted;
  the visual asymmetry is the host's contract that the feature is opt-in.
- Don't omit the `aria-label` on chevron buttons just because the icon is
  visually clear — AT users see no icon, only the accessible name.

---

## 13. Parity notes

- **Blazor (future HarborlinePager track):** consumes the same accessibility
  contract verbatim. Native `<button>` and `<select>` semantics carry across
  Blazor's interactive render mode.
- **React (this contract):** as documented.
- **Web Components (Phase M4, Lit):** TBD; the WC track will pass through
  native `<button>` and `<select>` shadowdom slots; live regions live in the
  light DOM or with explicit shadow-DOM exposure per WAI-ARIA guidance.

---

## References

- ADR 0017 §A1.3 — DataGrid contract scope (pager seam reservation)
- [Pager.Semantic.md](./Pager.Semantic.md) — prop contract
- [Pager.Interaction.md](./Pager.Interaction.md) — behavioural contract
- [Pager.Styling.md](./Pager.Styling.md) — token surface + visual states
- [DataGrid.Accessibility.md](./DataGrid.Accessibility.md) — composing surface
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `button`, `combobox`, `option`, `status`, `aria-live`, `aria-label`
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
