# DropDownTree — Styling Contract

- **Component:** DropDownTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownTree.Semantic.md) · [Interaction](./DropDownTree.Interaction.md) · [Accessibility](./DropDownTree.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownTree.tsx`
- **Catalog row:** #49 DropDownTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + host `className`

---

## 2. Trigger

`flex items-center px-3 gap-1 cursor-pointer` + fillClass + roundedClass + sizeClass

| Aspect | Classes |
|---|---|
| Size | `small`=`h-7 text-sm`, `medium`=`h-9 text-sm`, `large`=`h-11 text-base` |
| Fill | Same as TimePicker |
| Rounded | Standard scale |
| Disabled | `opacity-50 pointer-events-none` |

Display text: `flex-1 truncate`. Placeholder: `text-muted-foreground`. Chevron: `text-muted-foreground shrink-0`

---

## 3. Dropdown panel

`absolute z-50 w-full mt-1 bg-popover border border-border rounded-md shadow-md max-h-48 overflow-y-auto`

---

## 4. Tree node row

`flex items-center gap-1 py-1 px-2 cursor-pointer text-sm`
Indent: `paddingLeft: ${(depth+1) * 12}px`

| State | Classes |
|---|---|
| Selected | `bg-accent text-accent-foreground` |
| Default | `hover:bg-muted` |
| Disabled | `opacity-50 cursor-not-allowed` |

Expand/collapse indicator: `text-xs text-muted-foreground w-3` — shows `▾` (expanded) / `▸` (collapsed)
