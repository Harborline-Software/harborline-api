# Menu — Styling Contract

- **Component:** Menu
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Menu.Semantic.md) · [Interaction](./Menu.Interaction.md) · [Accessibility](./Menu.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Menu.tsx`
- **Catalog row:** #84 Menu (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root list

| Orientation | Classes |
|---|---|
| `horizontal` (default) | `flex flex-row items-center` |
| `vertical` | `flex flex-col` |

`className` prop is applied to the `<ul>`, not the `<nav>` wrapper.

---

## 2. Item element (per item `<a>` or `<button>`)

Base: `flex items-center gap-1.5 text-sm font-medium whitespace-nowrap transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1`

| Context | Additional classes |
|---|---|
| Root horizontal item (`depth=0`, `orientation='horizontal'`) | `px-3 py-2 rounded-md hover:bg-accent` |
| All other items | `w-full px-3 py-2 hover:bg-accent rounded-sm` |
| Disabled | `opacity-50 cursor-not-allowed pointer-events-none` |
| Custom (`cssClass`) | Appended after base and context classes |

---

## 3. Caret indicator

Appended inside the item element as a `<span className="ml-auto text-muted-foreground">`:

| Position | Caret glyph |
|---|---|
| Horizontal root parent (`isHorizontalRoot`) | `▾` (down-pointing) |
| Nested parent | `▸` (right-pointing) |

`ml-1` modifier applied to the caret span for horizontal root items.

---

## 4. Submenu list

`absolute z-50 min-w-[160px] py-1 bg-popover text-popover-foreground border border-border rounded-md shadow-md`

| Submenu depth | Position |
|---|---|
| Horizontal root → first level | `top-full left-0 mt-1` |
| Nested (any deeper level) | `top-0 left-full ml-0.5` |

---

## 5. Item `<li>` wrapper

`relative` — establishes positioning context for the submenu.

Non-horizontal-root items add `w-full`.

---

## 6. Color tokens

| Token | Usage |
|---|---|
| `bg-accent` | Hover / active background |
| `bg-popover` | Submenu background |
| `text-popover-foreground` | Submenu text |
| `border-border` | Submenu border |
| `text-muted-foreground` | Caret indicator color |
| `ring-ring` | Focus ring color |
