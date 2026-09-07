# ListBox — Styling Contract

- **Component:** ListBox
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListBox.Semantic.md) · [Interaction](./ListBox.Interaction.md) · [Accessibility](./ListBox.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/ListBox.tsx`
- **Catalog row:** #77 ListBox (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`flex gap-1` — list + optional toolbar side by side.

---

## 2. List `<ul>`

`min-h-[120px] w-full border border-input rounded-md overflow-y-auto bg-background`

---

## 3. Item `<li>`

Base: `px-3 py-2 text-sm cursor-pointer select-none focus-visible:outline-none`

| State | Additional classes |
|---|---|
| Selected | `bg-primary text-primary-foreground` |
| Unselected | `hover:bg-accent` |
| Disabled | `opacity-50 cursor-not-allowed` |
| Draggable (enabled) | `cursor-grab` |

---

## 4. Toolbar

`flex flex-col gap-1 justify-center`

Toolbar button (Move Up / Move Down / Remove):
`h-8 w-8 border border-input rounded hover:bg-accent text-sm flex items-center justify-center`

Remove button: additionally `text-destructive`
