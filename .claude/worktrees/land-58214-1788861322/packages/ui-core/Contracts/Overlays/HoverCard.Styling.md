# HoverCard — Styling Contract

- **Component:** HoverCard
- **ADR 0017 family:** Overlays
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./HoverCard.Semantic.md) · [Interaction](./HoverCard.Interaction.md) · [Accessibility](./HoverCard.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A2 HoverCard (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix @radix-ui/react-hover-card baseline)

---

## 1. Content panel (planned)

`z-50 w-64 rounded-md border border-border bg-popover p-4 text-popover-foreground shadow-md outline-none`

Animate in: `animate-in fade-in-0 zoom-in-95`

Animate out: `animate-out fade-out-0 zoom-out-95`

Side-based slide animations: `data-[side=bottom]:slide-in-from-top-2`, etc.

---

## 2. Design tokens

Uses design tokens throughout: `border-border`, `bg-popover`, `text-popover-foreground`. No hardcoded palette colors.

---

## 3. Width

Default: `w-64` (16rem). Callers override via `className`.
