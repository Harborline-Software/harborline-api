# Popover — Styling Contract

- **Component:** Popover
- **ADR 0017 family:** Overlays
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Popover.Semantic.md) · [Interaction](./Popover.Interaction.md) · [Accessibility](./Popover.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Popover.tsx`
- **Catalog row:** #98 Popover (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. PopoverContent

`z-50 min-w-[8rem] rounded-md border border-gray-200 bg-white p-4 shadow-md outline-none` + `className` passthrough.

> **M1 note:** `border-gray-200` and `bg-white` are hardcoded palette classes. Replace with `border-border bg-popover` in M2+.

Rendered inside a portal (`PopoverPrimitive.Portal`) at `document.body`.

---

## 2. Other exports

`Popover`, `PopoverTrigger`, `PopoverClose`, `PopoverAnchor` are unstyled Radix primitives — no default classes.

---

## 3. Positioning

Radix handles position calculation. Defaults: `align="center"`, `sideOffset=6`.
