# Pager — Interaction Contract

- **Component:** Pager
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Pager.Semantic.md) · [Styling](./Pager.Styling.md) · [Accessibility](./Pager.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Pager.tsx`
- **Catalog row:** #93 Pager (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

This contract describes **how Pager behaves in response to user input** — the
button-enabled states, the callback firings, and the bounds enforcement on
navigation. Visual styling and ARIA wiring are owned by the Styling and
Accessibility contracts (PAO).

Pager is **fully controlled**: it owns no state internally. Every user
interaction translates immediately into a callback invocation; the host is
expected to update `page` / `pageSize` and re-render.

---

## 2. Previous / Next navigation

- **Trigger:** clicking the Previous (`ChevronLeft`) or Next (`ChevronRight`)
  button. Buttons are also keyboard-activatable via Enter/Space when focused
  (standard `<button type="button">` behaviour).
- **Bounds enforcement:**
  - Previous button is `disabled` when `page === 1` (`canPrev = page > 1`).
  - Next button is `disabled` when `page === pageCount` (`canNext = page <
    pageCount`).
  - `pageCount = max(1, ceil(total / pageSize))`, so a Pager with `total === 0`
    has `pageCount === 1` and both buttons are disabled.
- **Callback:** clicking Previous fires `onPageChange(page - 1)`; clicking Next
  fires `onPageChange(page + 1)`. Out-of-bounds navigation cannot fire because
  the buttons are disabled at the bounds.
- **Disabled visual:** disabled buttons render with `opacity-40` and
  `cursor-not-allowed`. Tokens for these states are owned by PAO Styling.

---

## 3. Page-size selector

- **Trigger:** changing the selection in the `<select>` element labelled "Rows
  per page" (only rendered when `onPageSizeChange` is supplied).
- **Callback:** the `onChange` handler reads the new value, coerces it to a
  number (`Number(e.target.value)`), and fires `onPageSizeChange(newSize)`.
- **No companion `onPageChange`:** Pager does not auto-reset `page` to 1 when
  `pageSize` changes. The host owns the policy (Semantic §4).
- **Options:** the selector renders one `<option>` per value in
  `pageSizeOptions` (default `[10, 25, 50, 100]`). The current `pageSize` is
  the selected value; if the current `pageSize` is not in `pageSizeOptions`,
  the native `<select>` will display it as the placeholder-aligned value but
  no option will be visually selected (host responsibility to align).

---

## 4. Range summary ("Showing X–Y of Z")

- **Trigger:** any change in `page` / `pageSize` / `total` props.
- **Presentation:**
  - When `total === 0`: renders the string `"No results"`.
  - When `total > 0`: renders the string `"Showing {from}–{to} of {total}"`
    where:
    - `from = (page - 1) * pageSize + 1`
    - `to = min(page * pageSize, total)`
- **Live-region:** the range-summary span has `aria-live="polite"` so screen
  readers announce the new range on each pagination step. (See open
  question #3 in the Semantic contract.)

---

## 5. "Page X of Y" indicator

- **Presentation:** static text `"Page {page} of {pageCount}"` rendered between
  the Previous and Next buttons.
- **Live-region:** this span also carries `aria-live="polite"` so screen
  readers announce the new page on each pagination step.
- **No interaction:** the span is non-interactive — it cannot be clicked to
  open a "jump to page" affordance (deferred per Semantic §7).

---

## 6. Empty state (`total === 0`)

- **Buttons:** Previous and Next are both disabled (since `pageCount === 1`
  and `page === 1`).
- **Range summary:** displays `"No results"`.
- **Page-size selector:** if visible, remains interactive — hosts can change
  the page-size even when the current data set is empty (a subsequent fetch
  may return non-empty data at the new size).

---

## 7. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab / Shift+Tab | Move focus through Pager's interactive controls in DOM order: page-size selector (when visible) → Previous button → Next button. |
| Enter / Space (on Previous / Next) | Activate the button — same as click. |
| Enter / Space (on page-size selector) | Standard native `<select>` open / commit behaviour. |
| Arrow keys (on page-size selector) | Standard native `<select>` option cycling. |

Pager does not add custom keyboard shortcuts (PageUp / PageDown / Home / End)
in M1. Adding them is a deferred enhancement.

---

## 8. Interaction-state precedence

When multiple conditions hold, resolve in this order (highest precedence first):

1. **Empty** (`total === 0`) — both navigation buttons disabled; range summary
   shows "No results"; page-size selector (if visible) remains interactive.
2. **At first page** (`page === 1`, `total > 0`) — Previous disabled; Next
   interactive unless `pageCount === 1`.
3. **At last page** (`page === pageCount`, `total > 0`) — Next disabled;
   Previous interactive.
4. **Interior page** (`1 < page < pageCount`) — both navigation buttons
   interactive.

---

## 9. Council open questions (Interaction)

1. **Out-of-range page.** Semantic open question #1 has a behavioural mirror:
   if the host renders Pager with `page > pageCount` (data shrank), today the
   Next button is disabled (because `canNext = page < pageCount` is false) but
   so is Previous (because `page > 1` is true, but the page itself is
   invalid). The user is stranded — neither button advances toward a valid
   state. Should Pager auto-emit `onPageChange(pageCount)` on this condition?
2. **Loading state.** DataGrid has an `isLoading` skeleton mode. Pager has
   no equivalent. Should Pager accept `disabled?: boolean` so the host can
   disable the whole control during a fetch? (Leaning: yes, in a later wave;
   M1 keeps the current minimal surface.)
3. **Page-size selector keyboard shortcut.** Some grid libraries bind `0`
   (zero) or `*` to "go to first page" on the focused Pager. Worth adopting,
   or out of scope? (Leaning: out of scope for M1.)
