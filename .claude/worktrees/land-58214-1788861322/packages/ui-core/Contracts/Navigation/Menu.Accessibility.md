# Menu — Accessibility Contract

- **Component:** Menu
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Menu.Semantic.md) · [Interaction](./Menu.Interaction.md) · [Styling](./Menu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Menu.tsx`
- **Catalog row:** #84 Menu (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<nav aria-label="Menu">` | Root nav wrapper | Landmark with fixed label |
| `aria-haspopup={hasChildren}` | Item `<a>` / `<button>` | `true` (boolean) when item has children |
| `aria-expanded={hasChildren ? open : undefined}` | Item `<a>` / `<button>` | `true`/`false` for parent items; omitted on leaf items |
| `aria-disabled={item.disabled}` | Item `<a>` / `<button>` | `true` on disabled items |
| `disabled` | `<button>` only | HTML disabled attribute; not applicable to `<a>` |

---

## 2. Focus management

Items are standard `<a>` or `<button>` elements — they receive native browser focus in DOM order. No programmatic focus management on submenu open.

Focus ring: `focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1`

---

## 3. ARIA target pattern

This implementation targets the **Navigation landmark pattern** (not the ARIA Menu Widget pattern):
- Root uses `<nav aria-label>` (landmark)
- Items use `<a>` / `<button>` with `aria-haspopup` / `aria-expanded`
- No `role="menubar"` / `role="menu"` / `role="menuitem"` semantics (see G-MN7)
- No Arrow-key roving navigation (see G-MN2)

This distinction matters for the Gap G-MN2 keyboard model: the keyboard contract is Tab-based navigation (landmark traversal), not Arrow-key-based ARIA Menu Widget navigation. Implementors **must not** add Arrow-key roving without also adding `role="menubar"` and the full ARIA Menu Widget keyboard contract, as the two models are incompatible.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MN2 | High | No keyboard navigation — WAI-ARIA menubar/menu pattern requires Arrow key navigation, Escape to close, Enter/Space to activate | Blocking-before-v1-ship — WCAG SC 2.1.1 (Level A) violation; must resolve before v1 ship |
| G-MN6 | High | `aria-label="Menu"` is hardcoded — all Menu instances share the same landmark label; multiple Menus on one page create duplicate landmarks | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-MN7 | Medium | No `role="menubar"` / `role="menu"` / `role="menuitem"` — component uses `<nav>` + `<ul>` / `<li>` (list semantics, not menu widget semantics) | Accepted-risk M1 |
| G-MN8 | Medium | `aria-haspopup` receives a boolean `true` — ARIA spec prefers the string `"menu"` for menu submenus | Accepted-risk M1 |
| G-MN3 | Medium | No focus trap or focus management on submenu open — keyboard users Tab into submenus by following DOM order, not controlled navigation | Accepted-risk M1 |
| G-MN9 | Low | Disabled `<a>` items have no `tabIndex=-1` — they remain focusable despite being `aria-disabled` | Accepted-risk M1 |
