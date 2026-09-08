# Toolbar — Styling Contract

- **Component:** Toolbar
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Toolbar.Semantic.md) · [Interaction](./Toolbar.Interaction.md) · [Accessibility](./Toolbar.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Toolbar.tsx`
- **Catalog row:** #139 Toolbar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Container

Base: `flex w-full items-center bg-white` + size class + border class + `className` passthrough.

> **M1 note:** `bg-white` is hardcoded; `border-gray-200` (bottom border) is hardcoded.

---

## 2. Size classes

| `size` | Classes |
|---|---|
| `sm` | `h-10 px-3 gap-2` |
| `md` (default) | `h-12 px-4 gap-3` |

---

## 3. Border classes

| `border` | Classes |
|---|---|
| `none` (default) | *(empty)* |
| `bottom` | `border-b border-gray-200` |

---

## 4. Slots

| Slot | Wrapper classes |
|---|---|
| `leading` | `flex shrink-0 items-center gap-2` |
| `children` (center) | `flex flex-1 items-center gap-2 min-w-0` + `ml-2` (if `leading`) + `mr-2` (if `trailing`) |
| `trailing` | `flex shrink-0 items-center gap-2` |

The center region always renders (even when empty) so trailing stays right-anchored.
