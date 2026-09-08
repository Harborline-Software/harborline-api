# BottomNavigation — Accessibility Contract

- **Component:** BottomNavigation
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./BottomNavigation.Semantic.md) · [Interaction](./BottomNavigation.Interaction.md) · [Styling](./BottomNavigation.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/BottomNavigation.tsx`
- **Catalog row:** #12 BottomNavigation (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<nav>` | Container | Page-level navigation landmark |
| `<button type="button">` | Each item | Keyboard focusable, natural Tab order |
| `aria-current="page"` | Selected item button only | Current destination; omitted entirely on unselected items |
| `disabled` | Disabled item | Native HTML disabled |

`aria-selected` is **not** emitted anywhere in this component.

---

## 1a. Selection pattern — decision (earlier repository ticket #3396)

The M1 implementation emitted `aria-selected` on a plain `<button>`, which is invalid ARIA
(`aria-selected` is defined only for `option`, `row`, `tab`, `treeitem`, `gridcell`, `columnheader`,
`rowheader`). Two conformant patterns were available, and this contract records which was chosen and
why, because the choice determines the keyboard model as well as the markup.

**Chosen: `aria-current="page"` on the selected item, `<nav>` landmark retained, plain Tab order.**

**Rejected: the tab pattern (`role="tablist"` on the container, `role="tab"` on items).** Three
reasons, in order of weight:

1. **The component has no panels.** It reports a selected index through `onValueChange` and accepts no
   `aria-controls`; the host application decides what the selection means (typically a route change).
   `role="tab"` asserts a `tabpanel` relationship the component cannot fulfil, so it would replace one
   invalid-ARIA gap with a false one.
2. **`role="tablist"` would destroy the landmark.** An explicit role on the `<nav>` element overrides
   its implicit `navigation` role. G-BN2 recorded the `<nav>` landmark as the semantics the component
   actually provides; adopting `tablist` would remove it to describe the widget as something it is not.
3. **The tab pattern obliges a keyboard-behaviour change.** `tablist` requires roving `tabindex` plus
   Arrow-key traversal, which contradicts Interaction contract §3 (plain Tab order) — a behavioural
   change well beyond what correcting one attribute warrants, and a breaking change for any consumer
   that relies on tabbing through the bar.

`aria-current="page"` is additionally the pattern already in use by every sibling navigation component
in `@harborline-software/ui-react` — `Breadcrumb`, `Menu`, `NavDrawer` and `Pagination` — so adopting it here
removes a one-off rather than introducing one.

Verified by `BottomNavigation.test.tsx` §17–§20 (no `aria-selected` in the rendered DOM; exactly one
`aria-current="page"`; no `tabindex` on any item; no `tablist`/`tab` role) and by the axe pass over
`BottomNavigation.stories.tsx` in light and dark.

---

## 2. Badge accessibility

The badge `<span>` has no `aria-label` — its numeric value is not announced to AT. Screen reader users relying solely on AT will not hear badge counts.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BN1 | Medium | `aria-selected` on `<button>` is invalid ARIA (valid on tab/option/row/treeitem) | **CLOSED (earlier repository ticket #3396) — conformant.** Replaced by `aria-current="page"` on the selected item; see §1a |
| G-BN2 | Low | Container lacks `role="tablist"`; items lack `role="tab"` | **CLOSED (earlier repository ticket #3396) — not a gap.** The tab pattern is deliberately not adopted; §1a records the reasoning. `<nav>` + `aria-current` is the intended pattern, not a shortfall from one |
| G-BN3 | Medium | Badge counts not exposed to AT — no `aria-label` on badge span | Accepted-risk. Still open; out of scope for #3396 (bounded to the selection-state pattern) |
| G-BN4 | Serious | `fill="solid"` unselected label (`text-primary-foreground/60` over `bg-primary`) fails WCAG AA 1.4.3 — measured **2.73:1 light / 3.77:1 dark** against the 4.5:1 floor for 12px text | **OPEN — referred to Admiral, no disposition taken here.** Found while authoring the story for #3396; a *styling* defect, so #3396 (bounded to the selection-state pattern) prescribes no fix. Not solvable by raising the opacity: full-opacity `primary-foreground` over `primary` is only 5.00:1 in light, leaving no headroom for a visible unselected state. Needs a design decision (dedicated token, or weight/indicator instead of opacity). Until then no `fill="solid"` story exists — see the note in `BottomNavigation.stories.tsx` |
