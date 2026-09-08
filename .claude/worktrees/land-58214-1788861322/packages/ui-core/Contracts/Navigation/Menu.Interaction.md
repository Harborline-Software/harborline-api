# Menu — Interaction Contract

- **Component:** Menu
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Menu.Semantic.md) · [Accessibility](./Menu.Accessibility.md) · [Styling](./Menu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Menu.tsx`
- **Catalog row:** #84 Menu (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Hover mode (`openOnClick=false`)

| Event | Element | Effect |
|---|---|---|
| `mouseenter` | Parent item `<li>` | `setOpen(true)` |
| `mouseleave` | Parent item `<li>` | `setOpen(false)` |
| `mouseenter` | Submenu `<ul>` | `setOpen(true)` — keeps submenu open while pointer moves from `<li>` to submenu |
| `mouseleave` | Submenu `<ul>` | `setOpen(false)` |

---

## 2. Click mode (`openOnClick=true`)

| Event | Element | Condition | Effect |
|---|---|---|---|
| `click` | Item `<a>` or `<button>` | `hasChildren` | Toggle `open` |
| `click` | Item `<a>` or `<button>` | `!hasChildren && !disabled` | `onItemClick?.(item)` |
| `click` | Disabled item | `disabled` | No-op (early return) |

No click-outside-to-close handler exists — open submenus in click mode close only when the trigger is clicked again (see G-MN1).

---

## 3. Disabled items

- `<button>`: `disabled` attribute (prevents click) + CSS `pointer-events-none`
- `<a>`: No `disabled` attribute (links don't support it); `aria-disabled` only + CSS `pointer-events-none`

---

## 4. Keyboard contract (M1)

Menu M1 uses the **Navigation landmark keyboard model** (Tab traversal), not the ARIA Menu Widget model (Arrow-key roving). The M1 keyboard contract is:

- `Tab` / `Shift+Tab` — focus moves between menu items in DOM order (native browser behavior)
- `Enter` / `Space` — activates a focused `<button>` item (native button behavior); follows the `href` of a focused `<a>` item
- No Arrow-key navigation, no Escape-to-close, no Home/End — these are Gap G-MN2 and explicitly **not implemented in M1**

> Implementors MUST NOT add Arrow-key roving without simultaneously adding `role="menubar"` / `role="menu"` / `role="menuitem"` semantics (the full ARIA Menu Widget pattern). The two keyboard models are incompatible. See Menu.Accessibility.md §3 for the rationale.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MN1 | High | No click-outside-to-close in `openOnClick=true` mode — submenu stays open until re-clicked | Accepted-risk M1 |
| G-MN2 | High | No keyboard navigation — Arrow keys, Escape, Enter/Space not implemented | Accepted-risk M1 |
| G-MN3 | Medium | No focus management — keyboard Tab enters submenu items in DOM order, not menu-item order | Accepted-risk M1 |
| G-MN4 | Low | `orientation='vertical'` does not change submenu flyout direction — submenus always fly out to the right | Accepted-risk M1 |
| G-MN5 | Low | No close-on-scroll or close-on-focus-loss handler | Accepted-risk M1 |
