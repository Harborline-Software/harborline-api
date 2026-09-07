# ListToolbar — Accessibility Contract

- **Component:** ListToolbar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListToolbar.Semantic.md) · [Interaction](./ListToolbar.Interaction.md) · [Accessibility](./ListToolbar.Accessibility.md) · [Styling](./ListToolbar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ListToolbar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ListToolbar is a structural container. Its own accessibility surface is minimal — primarily the "Clear filters" button. The accessibility of its slot content is owned by the slot components.

---

## 2. Toolbar container

The reference implementation wraps slot content in a `<div>` with no landmark role.

**Recommendation:** The toolbar container SHOULD carry `role="toolbar"` and `aria-label="List filters and actions"` (or similar) so AT users can navigate to it as a landmark region.

**Known gap (A1):** `role="toolbar"` is missing. SR users cannot directly jump to the toolbar.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships; WAI-ARIA `toolbar` landmark.

---

## 3. "Clear filters" button

| Attribute | Value |
|---|---|
| Element | `<button type="button">` |
| Visible text | `"Clear filters"` |
| `aria-label` | None (visible text IS the accessible name) |

SR reads: `"Clear filters, button"`.

This is correct. No improvements needed.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Keyboard navigation

Tab order within ListToolbar is left-to-right DOM order:

1. Search slot content (SearchInput)
2. Filters slot content (FilterChips, selects, etc.)
3. Actions slot content (SortControl select, SortControl toggle, ColumnVisibilityMenu, SavedViewsMenu)
4. "Clear filters" button (when visible)

Each slot component manages its own internal tab order.

**WCAG citation:** WCAG 2.2 SC 2.4.3 Focus Order.

---

## 5. Slot accessibility delegation

ListToolbar delegates accessibility to its slot components:

| Slot | Accessibility contract |
|---|---|
| `search` | SearchInput — see SearchInput.Accessibility.md |
| `filters` | FilterChips / Filter.Accessibility.md |
| `actions` | SortControl / ColumnVisibilityMenu / SavedViewsMenu — see respective contracts |

---

## 6. Known gaps

| # | Item | Resolution path |
|---|---|---|
| A1 | Missing `role="toolbar"` on the container | Add `role="toolbar" aria-label="List filters and actions"` to the outer div |
| A2 | No skip-to-content mechanism for navigating past the toolbar | Host adds a skip link before ListToolbar |
