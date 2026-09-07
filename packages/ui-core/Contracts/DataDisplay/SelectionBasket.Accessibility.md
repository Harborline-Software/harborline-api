# SelectionBasket — Accessibility Contract

- **Component:** SelectionBasket
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (roles, ARIA, keyboard, SR behaviour)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SelectionBasket.Semantic.md) · [Interaction](./SelectionBasket.Interaction.md) · [Styling](./SelectionBasket.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/SelectionBasket.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Structure and roles

| Element | Role | Notes |
|---|---|---|
| Root | `section` (implicit `region`) | Named by `aria-label`, defaulting to the resolved title |
| Title | `heading` level 2 | Referenced by the root's `aria-describedby` |
| Group heading | `heading` level 3 | One per group; omitted entirely when ungrouped |
| Item list | `list` | One when flat; one **per group** when grouped |
| Item | `listitem` | — |
| Remove | `button` | — |
| Clear | `button` | — |

The heading levels are relative to a basket mounted in a page's main content. A host that
mounts it deeper is responsible for the surrounding document outline.

---

## 2. The count is announced exactly once

The visible badge is `aria-hidden="true"`; the count reaches assistive tech through an
adjacent visually-hidden `{count} selected` string. Without this split the heading reads
"Selection 3 3 selected" — the number twice, once with no unit.

The count string is a **CLDR plural pair** (`countOne` / `countOther`) resolved through
`Intl.PluralRules`, so locales with richer plural systems (Arabic, Polish, Russian) can supply
extra categories in their catalog without a code change.

---

## 3. Every remove control names its item

Each remove button's accessible name is `Remove {label}` (`feedback.removeItem`), not a bare
"Remove". A screen reader's elements list otherwise shows N identical "Remove" buttons with no
way to tell which is which — the exact failure mode that makes a list of per-row actions
unusable non-visually.

The `✕` glyph is `aria-hidden`; the accessible name carries the meaning.

---

## 4. Keyboard

The basket adds **no custom key handling**. Every control is a native `<button>`, so:

- `Tab` / `Shift+Tab` move through remove buttons and clear in DOM order.
- `Enter` / `Space` activate.
- Focus rings are `focus-visible`-scoped on every interactive element.

This is deliberate: the tray is a short list of buttons, and a roving-tabindex composite widget
(as `FilterChips` uses) would add a keyboard model users must learn for no gain. Nothing here is
a composite widget, so nothing claims to be one.

**Known consequence, stated plainly:** removing an item destroys the focused button. Focus
falls to the document body. Restoring focus to a sensible neighbour requires the host to know
what replaced it, so it is left to the host rather than guessed at here.

---

## 5. Empty state

The empty state is a plain paragraph, not a live region. The basket does not announce
transitions to/from empty — the user's own action caused it, and an unsolicited announcement on
every removal is noise.

---

## 6. Verification

- axe (Storybook a11y addon) over `Empty`, `Flat`, `Grouped`, `GroupedWithLabelLookup`,
  `Interactive`, and `Dark`.
- Unit tests assert the accessible names (`Remove Record 1`), the single count announcement,
  the heading levels, and that ungrouped rendering produces exactly one list.
