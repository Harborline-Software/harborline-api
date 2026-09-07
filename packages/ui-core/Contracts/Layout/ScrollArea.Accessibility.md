# ScrollArea — Accessibility Contract

- **Component:** ScrollArea
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScrollArea.Semantic.md) · [Interaction](./ScrollArea.Interaction.md) · [Styling](./ScrollArea.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A16 ScrollArea (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix ScrollArea baseline)

---

## 1. Viewport

The scrollable viewport is accessible via keyboard and has native scroll behavior — keyboard users scroll with arrow keys, Page Up/Down, Home, End.

---

## 2. Scrollbar

The custom scrollbar is visually decorative — Radix hides it from AT (`aria-hidden` on scrollbar elements). Native scrollable region semantics (via the viewport's overflow) communicate scrollability to screen readers.

---

## 3. Screen reader announcement

Screen readers announce the scrollable region via its overflow state. Callers may add `aria-label` to the ScrollArea root to label the scrollable region.

---

## 4. Known gaps

None identified for forward-spec.
