# FloatingLabel — Styling Contract

- **Component:** FloatingLabel
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FloatingLabel.Semantic.md) · [Interaction](./FloatingLabel.Interaction.md) · [Accessibility](./FloatingLabel.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FloatingLabel.tsx`
- **Catalog row:** #61 FloatingLabel (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Wrapper div

`relative` + `className` passthrough.

---

## 2. Child input classes (injected via cloneElement)

`peer block w-full rounded-md border border-input bg-background px-3 pb-2 pt-5 text-sm focus:outline-none focus:ring-2 focus:ring-ring`

Merged with the child's existing `className` via `cn()`.

> **M1 note:** Uses design tokens `border-input`, `bg-background`, `ring-ring` — not hardcoded colors.

---

## 3. Label classes

Base (always applied): `absolute left-3 top-3.5 z-10 origin-[0] -translate-y-2.5 scale-75 transform text-muted-foreground text-sm transition-all duration-200`

Unfocused + empty (placeholder position): `peer-placeholder-shown:translate-y-0 peer-placeholder-shown:scale-100`

Focused (floated up): `peer-focus:-translate-y-2.5 peer-focus:scale-75 peer-focus:text-ring`

---

## 4. Animation

CSS transform transition: `transition-all duration-200`. No JavaScript animation.

The label animates between two positions:
- **Placeholder position**: `translate-y-0 scale-100` (full size, vertically centered in input)
- **Floated position**: `scale-75 -translate-y-2.5` (75% size, moved to top of input)
