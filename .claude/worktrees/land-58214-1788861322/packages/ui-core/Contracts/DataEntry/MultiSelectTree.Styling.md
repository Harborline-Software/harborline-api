# MultiSelectTree — Styling Contract

- **Component:** MultiSelectTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiSelectTree.Semantic.md) · [Interaction](./MultiSelectTree.Interaction.md) · [Accessibility](./MultiSelectTree.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelectTree.tsx`
- **Catalog row:** #87 MultiSelectTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + host `className`

---

## 2. Trigger

`flex items-center px-3 gap-1 h-9 bg-white border border-input rounded-md cursor-pointer`
Disabled: `opacity-50 pointer-events-none`

Display text: `flex-1 text-sm truncate`. Placeholder: `text-muted-foreground`. Chevron: `text-muted-foreground shrink-0`

---

## 3. Dropdown panel

`absolute z-50 w-full mt-1 bg-popover border border-border rounded-md shadow-md max-h-56 overflow-y-auto`

---

## 4. Select All row (checkAll=true)

`flex items-center gap-2 px-3 py-1.5 border-b border-border`
Label: `text-xs font-medium`

---

## 5. Tree node row

`flex items-center gap-1 py-1 px-2 hover:bg-muted cursor-pointer`
Indent: `paddingLeft: ${(depth+1) * 12}px`

Expand button: `text-xs text-muted-foreground w-3 shrink-0` — `▾` expanded / `▸` collapsed
Item text: `text-sm` + `opacity-50` when disabled
