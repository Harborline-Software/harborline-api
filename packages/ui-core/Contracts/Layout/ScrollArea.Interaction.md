# ScrollArea — Interaction Contract

- **Component:** ScrollArea
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScrollArea.Semantic.md) · [Accessibility](./ScrollArea.Accessibility.md) · [Styling](./ScrollArea.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A16 ScrollArea (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix ScrollArea baseline)

---

## 1. Scroll behavior

ScrollArea preserves native scroll performance — uses `overflow: scroll` on the viewport element. The custom scrollbar is a visual overlay; native scroll events fire normally.

---

## 2. Scrollbar interaction

The custom scrollbar thumb supports drag to scroll. Dragging the thumb calls a scroll position update on the viewport.

---

## 3. Keyboard scrolling

Arrow keys, Page Up/Down, Home/End scroll the viewport when focus is within the scroll area — native browser scroll-key behavior applies.

---

## 4. Known gaps

None identified for forward-spec.
