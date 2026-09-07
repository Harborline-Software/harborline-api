# ScrollArea — Styling Contract

- **Component:** ScrollArea
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScrollArea.Semantic.md) · [Interaction](./ScrollArea.Interaction.md) · [Accessibility](./ScrollArea.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A16 ScrollArea (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix ScrollArea baseline)

---

## 1. Root

`relative overflow-hidden` + `className` passthrough.

---

## 2. Viewport

`h-full w-full rounded-[inherit]`

Underlying: `overflow: scroll` on the native scrollable element (hidden via CSS to prevent double scrollbars).

---

## 3. Scrollbar

`flex touch-none select-none transition-colors`

Vertical: `h-full w-2.5 border-l border-l-transparent p-[1px]`

Horizontal: `h-2.5 flex-row border-t border-t-transparent p-[1px]`

---

## 4. Thumb

`relative flex-1 rounded-full bg-border`

---

## 5. Corner

`bg-background`

---

## 6. Design tokens

Uses `bg-border`, `bg-background` — design-token-backed.
