# Transfer — Styling Contract

- **Component:** Transfer / ShuttleList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Transfer.Semantic.md) · [Interaction](./Transfer.Interaction.md) · [Accessibility](./Transfer.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A8 Transfer / ShuttleList (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Transfer baseline)

---

## 1. Root container

`flex items-stretch gap-3`

---

## 2. List panel

`flex flex-col rounded-md border border-border bg-background` + `listStyle` prop override.

Default width: `min-w-[180px]`

---

## 3. Panel header

`flex items-center gap-2 border-b border-border px-3 py-2`

Title: `text-sm font-medium text-foreground`

Item count: `text-xs text-muted-foreground`

---

## 4. Panel body

`flex-1 overflow-y-auto`

---

## 5. List items

Base: `flex items-center gap-2 px-3 py-2 text-sm cursor-pointer hover:bg-accent`

Selected: `bg-accent text-accent-foreground`

Disabled: `opacity-50 cursor-not-allowed`

---

## 6. Operation buttons column

`flex flex-col items-center justify-center gap-2`

Buttons: `Button variant="outline" size="sm"` with arrow icon.

Disabled state: `opacity-50 pointer-events-none`

---

## 7. Search input

`border-b border-border px-3 py-2` wrapping an `Input` component.

---

## 8. Design tokens

Uses design tokens: `border-border`, `bg-background`, `text-foreground`, `bg-accent`, `text-accent-foreground`, `text-muted-foreground`.
