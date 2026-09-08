# RoleGate — Accessibility Contract

- **Component:** RoleGate
- **ADR 0017 family:** Utility
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RoleGate.Semantic.md) · [Interaction](./RoleGate.Interaction.md) · [Accessibility](./RoleGate.Accessibility.md) · [Styling](./RoleGate.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/RoleGate.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Accessibility model

RoleGate adds no HTML elements to the DOM. It renders `children` or `fallback` as React fragments. There are no ARIA roles, attributes, or DOM elements introduced by RoleGate itself.

The accessibility characteristics of the rendered output are entirely determined by `children` or `fallback`.

---

## 2. Screen reader behaviour

- When a user's role is permitted: `children` renders exactly as if RoleGate were not present.
- When a user's role is not permitted: `fallback` (or nothing) renders. Content that would have been in `children` is simply absent from the DOM — screen readers have no awareness that gated content exists.

---

## 3. Accessibility considerations for hosts

**Do NOT use RoleGate to hide content that should remain accessible.** When content is absent from the DOM, screen reader users with appropriate roles will simply not find it — this is the intended behaviour for access control.

**Fallback accessibility:** When `fallback` is provided (e.g. a read-only view instead of an edit form), the fallback must itself be accessible. RoleGate does not add any accessible context to the fallback.

---

## 4. Known gaps

None specific to RoleGate itself. The component is a zero-DOM utility; accessibility is delegated entirely to the children and fallback.
