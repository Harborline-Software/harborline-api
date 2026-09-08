# RoleGate — Styling Contract

- **Component:** RoleGate
- **ADR 0017 family:** Utility
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RoleGate.Semantic.md) · [Interaction](./RoleGate.Interaction.md) · [Accessibility](./RoleGate.Accessibility.md) · [Styling](./RoleGate.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/RoleGate.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. RoleGate has no styling

RoleGate renders only React fragments (`<>{children}</>` or `<>{fallback}</>`). It introduces no HTML elements, no CSS classes, no inline styles, and no CSS variables.

All styling of the rendered content is owned by `children` or `fallback`.

---

## 2. No theming surface

There are no Tailwind classes, CSS custom properties, or design-token references in RoleGate. It is a pure logic utility.
